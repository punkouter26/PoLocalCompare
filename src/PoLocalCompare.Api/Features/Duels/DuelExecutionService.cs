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
    public static partial void DuelNotFound(ILogger logger, DuelId duelId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Error, Message = "One or both models not found for duel {DuelId}.")]
    public static partial void ModelsNotFound(ILogger logger, DuelId duelId);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Error, Message = "Duel execution failed for {DuelId}.")]
    public static partial void ExecutionFailed(ILogger logger, Exception ex, DuelId duelId);
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

    /// <param name="autoJudgeDelaySecondsOverride">
    /// Replaces the configured grace window for this duel only — demo mode passes 0 so the run
    /// does not pause between rounds waiting for a human who is not there.
    /// </param>
    public Task EnqueueAsync(DuelId duelId, int? autoJudgeDelaySecondsOverride = null)
    {
        // Queue the execution task for reliable background processing
        _taskQueue.QueueBackgroundWork(async ct =>
        {
            using var scope = _scopeFactory.CreateScope();
            await ExecuteAsync(scope.ServiceProvider, duelId, autoJudgeDelaySecondsOverride, ct);
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the duel end-to-end on the calling thread, without going through the background
    /// task queue. Used by the tournament runner, which already owns a long-lived wait loop
    /// and would otherwise deadlock: <see cref="BackgroundTaskService"/> is single-consumer
    /// and awaits each work item before dequeuing the next, so a runner that queues a duel
    /// behind itself stalls forever.
    /// </summary>
    /// <remarks>
    /// The runner used to share the queue with everything else and stalled at the first
    /// match; running inline keeps the bracket on its own thread but lets the duel inference
    /// finish, which is the whole point. The runner is responsible for its own concurrency
    /// limit (<see cref="Tournaments.TournamentRunner"/> caps it at two), so the single-
    /// threaded queue is not a bottleneck here.
    /// </remarks>
    public async Task RunAsync(DuelId duelId, int? autoJudgeDelaySecondsOverride, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        await ExecuteAsync(scope.ServiceProvider, duelId, autoJudgeDelaySecondsOverride, cancellationToken);
    }

    private async Task ExecuteAsync(
        IServiceProvider services,
        DuelId duelId,
        int? autoJudgeDelaySecondsOverride,
        CancellationToken cancellationToken)
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

            var lobby = services.GetRequiredService<LobbyNotifier>();
            await lobby.DuelStartedAsync(duel, leftModel.DisplayName, rightModel.DisplayName, cancellationToken);

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

            await _hubContext.Clients
                .Group($"duel:{duelId}")
                .SendAsync("DuelComplete", new DuelDto
                {
                    DuelId = duel.DuelId,
                    PromptText = duel.PromptText,
                    PromptFull = duel.PromptFull,
                    LeftModelId = duel.LeftModelId,
                    RightModelId = duel.RightModelId,
                    LeftModelName = leftModel.DisplayName,
                    RightModelName = rightModel.DisplayName,
                    StartedAt = duel.StartedAt,
                    CompletedAt = duel.CompletedAt,
                    Verdict = DuelVerdict.Pending,
                    TimeLimitSeconds = 900,
                    OwnerId = duel.OwnerId,
                    VerdictBy = duel.VerdictBy,
                });

            await lobby.DuelCompletedAsync(duel, leftModel.DisplayName, rightModel.DisplayName, cancellationToken);

            // A challenge budget is arithmetic over the result rows, so it is settled before
            // anything reads the outputs — and without the LLM, which means it keeps working
            // with AiJudge:Enabled=false. It returns true only when the budget actually decided
            // the duel; a budget both models met separates nothing and falls through to the
            // ordinary judge below.
            var decidedByBudget = await services.GetRequiredService<ChallengeAdjudicator>()
                .TryAdjudicateAsync(duelId, cancellationToken);

            // Hand off to the auto-judge, which waits out the grace window before deciding.
            // Run inline rather than as a second queued item: BackgroundTaskService awaits each
            // work item before dequeuing the next, so a queued delay would stall the next duel.
            // The duel is not finished until it has a verdict, so blocking here is the honest
            // shape — and AutoJudge.RunAsync never throws.
            if (!decidedByBudget)
            {
                await services.GetRequiredService<AutoJudge>()
                    .RunAsync(duelId, cancellationToken, autoJudgeDelaySecondsOverride);
            }
        }
        catch (Exception ex)
        {
            DuelExecutionLog.ExecutionFailed(_logger, ex, duelId);
        }
    }

    private async Task RunModelAsync(
        DuelId duelId,
        Model model,
        string side,
        string promptFull,
        IRemoteInferenceProxy inferenceProxy,
        IDuelResultRepository duelResultRepo,
        CancellationToken cancellationToken)
    {
        await SendStatusAsync(duelId, model.ModelId, side, DuelStatus.Initializing, 0, 0);

        DuelResult result;

        // Defensive catch: every exit must produce a saved DuelResult row. Without this, an
        // exception thrown inside RunInferenceAsync (or WaitForLocalModelResultAsync) after the
        // proxy had already assigned `result.IsFailure = true` but before it returned would
        // surface from the task — `Task.WhenAll` aggregates and the outer catch is fine — but if
        // it left without writing the row, the duel would strand as "in progress, not judged"
        // forever (PRD §9: ELO cannot move on no evidence, and one-sided failures take the
        // walkover path inside AutoJudge, both of which require both result rows to exist).
        try
        {
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Watchdog tripped or host shutdown — synthesise a failure row so the duel can
            // still transition to Complete and the auto-judge can run (probably standing down
            // because one side has no usable output, which is the documented behaviour).
            result = new DuelResult(duelId, model.ModelId)
            {
                IsFailure = true,
                FailureReason = "Inference cancelled by watchdog (900s).",
                TotalDurationMs = 900_000,
            };
        }
        catch (Exception ex)
        {
            // Any other unexpected exception from inside inference must not strand the duel.
            // The catch in ExecuteAsync only sees exceptions surfaced through Task.WhenAll,
            // which is fine for completion; here we ensure every exit writes a row.
            result = new DuelResult(duelId, model.ModelId)
            {
                IsFailure = true,
                FailureReason = $"Inference crashed: {ex.GetType().Name}: {ex.Message}",
                TotalDurationMs = 0,
            };
            _logger.LogWarning(ex, "Inference crashed for {Side} ({ModelId}); recording failure row.", side, model.ModelId);
        }

        if (result.IsFailure)
        {
            await SendStatusAsync(duelId, model.ModelId, side,
                DuelStatus.Failed, result.TotalDurationMs, result.TokenCount);
        }
        else
        {
            // The finished output rides out with the Done status rather than waiting for
            // DuelComplete: that message only fires once *both* sides are in, so until then a
            // model that crossed the line would still be showing its last mid-stream preview —
            // truncated ~25 tokens short of the ending, or blank if it never emitted one.
            await SendStatusAsync(duelId, model.ModelId, side,
                DuelStatus.Done, result.TotalDurationMs, result.TokenCount,
                finalHtml: Truncate(result.HtmlOutputRaw));
        }

        // Character density + quality + GreenStats enrichment (shared Domain policy).
        var electricityRate = _configuration.GetValue("GreenStats:ElectricityRateUsd", 0.12);
        DuelResultEnricher.Enrich(result, model, electricityRate);

        await duelResultRepo.SaveAsync(result);
    }

    private async Task<DuelResult> WaitForLocalModelResultAsync(
        DuelId duelId,
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

        // One scope for the whole poll, not one per 500ms tick — this loop can run for the
        // full 15-minute watchdog, which was ~1,800 scope creations and repository resolutions.
        using var pollScope = _scopeFactory.CreateScope();
        var duelResultRepo = pollScope.ServiceProvider.GetRequiredService<IDuelResultRepository>();

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

    /// <summary>
    /// Ceiling on the completed document pushed over the hub. Generous next to the 5 KB
    /// mid-stream preview — this one is the finished render a person looks at — but still
    /// bounded, because a runaway model can emit far more than any preview needs to show.
    /// The persisted result is never truncated; only this hub frame is.
    /// </summary>
    private const int FinalHtmlMaxChars = 40_000;

    private static string? Truncate(string? html) =>
        html is { Length: > FinalHtmlMaxChars } ? html[..FinalHtmlMaxChars] : html;

    private Task SendStatusAsync(
        DuelId duelId,
        ModelId modelId,
        string side,
        DuelStatus status,
        long elapsedMs,
        int tokenCount,
        long? warmUpMs = null,
        double? peakVelocity = null,
        bool isStalled = false,
        HtmlStreamStats? htmlStats = null,
        string? finalHtml = null) =>
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
                // The Done frame carries the whole output and no counters; the streaming frames
                // carry counters and a partial. Neither ever sets both.
                HtmlPreview = finalHtml ?? htmlStats?.HtmlPreview,
            });
}