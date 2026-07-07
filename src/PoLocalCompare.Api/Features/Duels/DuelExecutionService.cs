// GoF: Strategy — inference execution varies by model type
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

/// <summary>Source-generated, allocation-free log messages for the duel execution hot path.</summary>
internal static partial class DuelExecutionLog
{
    [LoggerMessage(EventId = 1100, Level = LogLevel.Warning, Message = "Duel {DuelId} not found for execution.")]
    public static partial void DuelNotFound(ILogger logger, string duelId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "One or both models not found for duel {DuelId}.")]
    public static partial void ModelsNotFound(ILogger logger, string duelId);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Duel execution failed for {DuelId}.")]
    public static partial void ExecutionFailed(ILogger logger, Exception ex, string duelId);
}

public sealed class DuelExecutionService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<DuelHub> _hubContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DuelExecutionService> _logger;
    private readonly IBackgroundTaskQueue _taskQueue;

    public DuelExecutionService(
        IServiceScopeFactory scopeFactory,
        IHubContext<DuelHub> hubContext,
        IConfiguration configuration,
        ILogger<DuelExecutionService> logger,
        IBackgroundTaskQueue taskQueue)
    {
        _scopeFactory = scopeFactory;
        _hubContext = hubContext;
        _configuration = configuration;
        _logger = logger;
        _taskQueue = taskQueue;
    }

    public Task EnqueueAsync(string duelId)
    {
        // Queue the execution task for reliable background processing
        _taskQueue.QueueBackgroundWork(async ct =>
        {
            using var scope = _scopeFactory.CreateScope();
            await ExecuteAsync(scope.ServiceProvider, duelId, ct);
        });
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(IServiceProvider services, string duelId, CancellationToken cancellationToken)
    {
        var duelRepo = services.GetRequiredService<IDuelRepository>();
        var modelRepo = services.GetRequiredService<IModelRepository>();
        var duelResultRepo = services.GetRequiredService<IDuelResultRepository>();

        IRemoteInferenceProxy ResolveProxy(Model model) => model.ModelType switch
        {
            ModelType.LocalService => services.GetRequiredKeyedService<IRemoteInferenceProxy>("LocalService"),
            _ => services.GetRequiredKeyedService<IRemoteInferenceProxy>("Remote")
        };

        Duel? duel = null;
        try
        {
            duel = await duelRepo.GetByIdAsync(duelId);
            if (duel is null)
            {
                DuelExecutionLog.DuelNotFound(_logger, duelId);
                return;
            }

            var leftModel = await modelRepo.GetByIdAsync(duel.LeftModelId);
            var rightModel = await modelRepo.GetByIdAsync(duel.RightModelId);
            if (leftModel is null || rightModel is null)
            {
                DuelExecutionLog.ModelsNotFound(_logger, duelId);
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
            try
            {
                await duelRepo.UpdateAsync(duel);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412)
            {
                // A verdict landed while inference ran (standards §5.5): re-read and
                // reapply only the completion timestamp instead of clobbering the verdict.
                var fresh = await duelRepo.GetByIdAsync(duelId);
                if (fresh is not null)
                {
                    fresh.CompletedAt ??= duel.CompletedAt;
                    await duelRepo.UpdateAsync(fresh);
                    duel = fresh;
                }
            }

            // Forfeit rule: if exactly one model produced no output, auto-award the
            // survivor so the duel resolves without asking a human to "judge" a no-contest.
            // Both-succeeded (needs a human verdict) and both-failed stay Pending.
            if (duel.Verdict == DuelVerdict.Pending)
            {
                var leftResult  = await duelResultRepo.GetAsync(duelId, duel.LeftModelId);
                var rightResult = await duelResultRepo.GetAsync(duelId, duel.RightModelId);
                if (leftResult is not null && rightResult is not null && (leftResult.IsFailure ^ rightResult.IsFailure))
                {
                    var forfeitVerdict = leftResult.IsFailure ? DuelVerdict.Right : DuelVerdict.Left;
                    try
                    {
                        var verdictHandler = services.GetRequiredService<RecordVerdictHandler>();
                        await verdictHandler.HandleAsync(new RecordVerdictCommand(duelId, forfeitVerdict));
                        _logger.LogInformation("Duel {DuelId}: one model failed — auto-awarded {Verdict} by forfeit.", duelId, forfeitVerdict);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Duel {DuelId}: forfeit auto-award failed; leaving pending for manual judgment.", duelId);
                    }
                }
            }

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
                    Verdict = DuelVerdict.Pending,
                    TimeLimitSeconds = 900,
                });
        }
        catch (Exception ex)
        {
            DuelExecutionLog.ExecutionFailed(_logger, ex, duelId);
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
        await SendStatusAsync(duelId, model.ModelId, side, DuelStatus.Initializing, 0, 0);

        DuelResult result;

        if (model.ModelType == ModelType.Local)
        {
            // Local model: client-side inference via SignalR; server waits for client to report results
            result = await WaitForLocalModelResultAsync(duelId, model, side, cancellationToken);
        }
        else
        {
            // Remote model: server-side inference via Foundry proxy
            await SendStatusAsync(duelId, model.ModelId, side, DuelStatus.Generating, 0, 0);

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
                        DuelStatus.Generating, elapsedMs, tokenCount,
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
                DuelStatus.Failed, result.TotalDurationMs, result.TokenCount);
        }
        else
        {
            await SendStatusAsync(duelId, model.ModelId, side,
                DuelStatus.Done, result.TotalDurationMs, result.TokenCount);
        }

        // Character density + quality + GreenStats enrichment (shared Domain policy).
        var electricityRate = _configuration.GetValue("GreenStats:ElectricityRateUsd", 0.12);
        DuelResultEnricher.Enrich(result, model, electricityRate);

        await duelResultRepo.SaveAsync(result);
    }

    private async Task<DuelResult> WaitForLocalModelResultAsync(
        string duelId,
        Model model,
        string side,
        CancellationToken cancellationToken)
    {
        var payload = new { duelId, modelId = model.ModelId, side, webLlmModelId = model.WebLlmModelId };

        // Signal the client to start its Web Worker inference.
        // Retry every 5 s — the client may not have joined the SignalR group yet when the first send fires.
        await _hubContext.Clients
            .Group($"duel:{duelId}")
            .SendAsync("StartLocalInference", payload, cancellationToken);

        // The client will report back via POST /api/duels/{duelId}/local-result
        // We poll the result repository until the result appears or watchdog fires
        var result = new DuelResult(duelId, model.ModelId)
        {
            IsFailure = false,
        };

        var started = DateTimeOffset.UtcNow;
        var lastSignalAt = DateTimeOffset.UtcNow;
        const int retryIntervalMs = 5_000;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(500, cancellationToken);

            var elapsed = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            await SendStatusAsync(duelId, model.ModelId, side, DuelStatus.Generating, elapsed, 0);

            // Resend StartLocalInference periodically so late-joining clients receive it
            if ((DateTimeOffset.UtcNow - lastSignalAt).TotalMilliseconds >= retryIntervalMs)
            {
                lastSignalAt = DateTimeOffset.UtcNow;
                await _hubContext.Clients
                    .Group($"duel:{duelId}")
                    .SendAsync("StartLocalInference", payload, cancellationToken);
            }

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
        DuelStatus status,
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
}