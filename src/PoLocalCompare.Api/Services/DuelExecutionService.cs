// GoF: Strategy — inference execution varies by model type
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Api.Hubs;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Domain.Enums;
using PoLocalCompare.Domain.Services;
using PoLocalCompare.Shared.DTOs;
using System.Text.RegularExpressions;
using SharedDuelStatus = PoLocalCompare.Shared.Enums.DuelStatus;
using SharedDuelVerdict = PoLocalCompare.Shared.Enums.DuelVerdict;

namespace PoLocalCompare.Api.Services;

public sealed class DuelExecutionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DuelHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DuelExecutionService> _logger;

    public DuelExecutionService(
        IServiceScopeFactory scopeFactory,
        IHubContext<DuelHub> hubContext,
        IConfiguration configuration,
        ILogger<DuelExecutionService> logger)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _configuration = configuration;
        _logger = logger;
    }

    public Task EnqueueAsync(string duelId)
    {
        // Fire-and-forget on a thread pool thread; errors are logged
        _ = Task.Run(() => ExecuteAsync(duelId));
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string duelId)
    {
        using var scope = _scopeFactory.CreateScope();
        var duelRepo = scope.ServiceProvider.GetRequiredService<IDuelRepository>();
        var modelRepo = scope.ServiceProvider.GetRequiredService<IModelRepository>();
        var duelResultRepo = scope.ServiceProvider.GetRequiredService<IDuelResultRepository>();

        IRemoteInferenceProxy ResolveProxy(Model model) => model.ModelType switch
        {
            ModelType.LocalService => scope.ServiceProvider.GetRequiredKeyedService<IRemoteInferenceProxy>("LocalService"),
            _ => scope.ServiceProvider.GetRequiredKeyedService<IRemoteInferenceProxy>("Remote")
        };

        Duel? duel = null;
        try
        {
            duel = await duelRepo.GetByIdAsync(duelId);
            if (duel is null)
            {
                _logger.LogWarning("Duel {DuelId} not found for execution.", duelId);
                return;
            }

            var leftModel = await modelRepo.GetByIdAsync(duel.LeftModelId);
            var rightModel = await modelRepo.GetByIdAsync(duel.RightModelId);
            if (leftModel is null || rightModel is null)
            {
                _logger.LogError("One or both models not found for duel {DuelId}.", duelId);
                return;
            }

            // 900-second watchdog (15 min) — allows for WebGPU shader JIT compilation on first run
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(900));

            var leftTask = RunModelAsync(duelId, leftModel, "Left", duel.PromptFull,
                ResolveProxy(leftModel), duelResultRepo, watchdog.Token);
            var rightTask = RunModelAsync(duelId, rightModel, "Right", duel.PromptFull,
                ResolveProxy(rightModel), duelResultRepo, watchdog.Token);

            await Task.WhenAll(leftTask, rightTask);

            duel.CompletedAt = DateTimeOffset.UtcNow;
            await duelRepo.UpdateAsync(duel);

            await _hubContext.Clients
                .Group($"duel:{duelId}")
                .SendAsync("DuelComplete", new DuelDto
                {
                    DuelId = duel.DuelId,
                    PromptText = duel.PromptText,
                    PromptFull = duel.PromptFull,
                    LeftModelId = duel.LeftModelId,
                    RightModelId = duel.RightModelId,
                    StartedAt = duel.StartedAt,
                    CompletedAt = duel.CompletedAt,
                    Verdict = SharedDuelVerdict.Pending,
                    TimeLimitSeconds = 900,
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Duel execution failed for {DuelId}.", duelId);
        }
    }

    private async Task RunModelAsync(
        string duelId,
        Model model,
        string side,
        string promptFull,
        IRemoteInferenceProxy inferenceProxy,
        IDuelResultRepository duelResultRepo,
        CancellationToken cancellationToken)
    {
        await SendStatusAsync(duelId, model.ModelId, side, SharedDuelStatus.Initializing, 0, 0);

        DuelResult result;

        if (model.ModelType == Domain.Enums.ModelType.Local)
        {
            // Local model: client-side inference via SignalR; server waits for client to report results
            result = await WaitForLocalModelResultAsync(duelId, model, side, cancellationToken);
        }
        else
        {
            // Remote model: server-side inference via Foundry proxy
            await SendStatusAsync(duelId, model.ModelId, side, SharedDuelStatus.Generating, 0, 0);

            long? warmUpMs = null;
            double peakVelocity = 0;
            DateTimeOffset lastTokenAt = DateTimeOffset.UtcNow;

            result = await inferenceProxy.RunInferenceAsync(
                model,
                duelId,
                promptFull,
                async (tokenCount, elapsedMs, htmlStats) =>
                {
                    // First token arrival = warm-up latency known
                    if (warmUpMs is null && tokenCount >= 1)
                        warmUpMs = elapsedMs;

                    lastTokenAt = DateTimeOffset.UtcNow;

                    // Peak velocity (generation-phase only, excluding warm-up)
                    var genMs = warmUpMs.HasValue ? elapsedMs - warmUpMs.Value : elapsedMs;
                    var currentVelocity = genMs > 0 ? Math.Round(tokenCount / (genMs / 1000.0), 1) : 0;
                    if (currentVelocity > peakVelocity) peakVelocity = currentVelocity;

                    var isStalled = (DateTimeOffset.UtcNow - lastTokenAt).TotalSeconds > 2;

                    await SendStatusAsync(duelId, model.ModelId, side,
                        SharedDuelStatus.Generating, elapsedMs, tokenCount,
                        warmUpMs: warmUpMs,
                        peakVelocity: peakVelocity,
                        isStalled: isStalled,
                        htmlStats: htmlStats);
                },
                cancellationToken);
        }

        if (result.IsFailure)
        {
            await SendStatusAsync(duelId, model.ModelId, side,
                SharedDuelStatus.Failed, result.TotalDurationMs, result.TokenCount);
        }
        else
        {
            await SendStatusAsync(duelId, model.ModelId, side,
                SharedDuelStatus.Done, result.TotalDurationMs, result.TokenCount);
        }

        // T065 — character density + GreenStats enrichment
        EnrichResult(result, model);

        await duelResultRepo.SaveAsync(result);
    }

    private async Task<DuelResult> WaitForLocalModelResultAsync(
        string duelId,
        Model model,
        string side,
        CancellationToken cancellationToken)
    {
        // Signal the client to start its Web Worker inference
        await _hubContext.Clients
            .Group($"duel:{duelId}")
            .SendAsync("StartLocalInference", new { duelId, modelId = model.ModelId, side, webLlmModelId = model.WebLlmModelId }, cancellationToken);

        // The client will report back via POST /api/duels/{duelId}/local-result
        // We poll the result repository until the result appears or watchdog fires
        var result = new DuelResult(duelId, model.ModelId)
        {
            IsFailure = false,
        };

        var started = DateTimeOffset.UtcNow;
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);

            var elapsed = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            await SendStatusAsync(duelId, model.ModelId, side, SharedDuelStatus.Generating, elapsed, 0);

            // Check if result was posted by client
            using var scope = _scopeFactory.CreateScope();
            var duelResultRepo = scope.ServiceProvider.GetRequiredService<IDuelResultRepository>();
            var stored = await duelResultRepo.GetAsync(duelId, model.ModelId);
            if (stored is not null)
                return stored;
        }

        // Watchdog expired
        result.IsFailure = true;
        result.FailureReason = "Watchdog timeout (900s)";
        result.TotalDurationMs = 900_000;
        return result;
    }

    private Task SendStatusAsync(
        string duelId,
        string modelId,
        string side,
        SharedDuelStatus status,
        long elapsedMs,
        int tokenCount,
        string? detail = null,
        long? warmUpMs = null,
        double? peakVelocity = null,
        bool isStalled = false,
        HtmlStreamStats? htmlStats = null) =>
        _hubContext.Clients
            .Group($"duel:{duelId}")
            .SendAsync("ModelStatusUpdate", new ModelStatusUpdateDto
            {
                DuelId = duelId,
                ModelId = modelId,
                Side = side,
                Status = status,
                ElapsedMs = elapsedMs,
                TokenCount = tokenCount,
                WarmUpMs = warmUpMs,
                TokenVelocity = warmUpMs.HasValue && elapsedMs > warmUpMs
                    ? Math.Round(tokenCount / ((elapsedMs - warmUpMs.Value) / 1000.0), 1)
                    : (elapsedMs > 0 ? Math.Round(tokenCount / (elapsedMs / 1000.0), 1) : null),
                PeakTokenVelocity = peakVelocity,
                IsStalled = isStalled,
                HtmlTagCount = htmlStats?.TagCount,
                OpenTagDepth = htmlStats?.OpenDepth,
                StyleRuleCount = htmlStats?.StyleRules,
                RepetitionScore = htmlStats?.RepetitionScore,
                Detail = detail,
                HtmlPreview = htmlStats?.HtmlPreview,
            });

    /// <summary>
    /// Computes character density ratio and (for local models) GreenStats.
    /// Mutates the result in place — called before persisting.
    /// </summary>
    private void EnrichResult(DuelResult result, Model model)
    {
        // Character density: strip HTML comments, collapse whitespace, count functional chars
        var html = result.HtmlOutputRaw ?? string.Empty;
        var noComments = Regex.Replace(html, @"<!--.*?-->", string.Empty, RegexOptions.Singleline);
        var collapsed = Regex.Replace(noComments, @"\s+", " ").Trim();
        var totalBytes = System.Text.Encoding.UTF8.GetByteCount(html);
        if (totalBytes > 0)
        {
            var nonWhitespace = collapsed.Replace(" ", string.Empty).Length;
            result.CharacterDensityRatio = Math.Round((double)nonWhitespace / totalBytes, 4);
        }

        // GreenStats (local models and local-service models with TdpWatts set)
        if ((model.ModelType == ModelType.Local || model.ModelType == ModelType.LocalService)
            && model.TdpWatts.HasValue && !result.IsFailure)
        {
            var rateUsd = _configuration.GetValue<double>("GreenStats:ElectricityRateUsd", 0.12);
            var energyWh = GreenStatsCalculator.ComputeEnergyWh(model.TdpWatts.Value, result.TotalDurationMs);
            result.EnergyWh = energyWh;
            result.EnergyCostUsd = GreenStatsCalculator.ComputeEnergyCostUsd(energyWh, rateUsd);
            result.GreenScore = GreenStatsCalculator.ComputeGreenScore(result.TokenCount, energyWh);
        }
    }
}

