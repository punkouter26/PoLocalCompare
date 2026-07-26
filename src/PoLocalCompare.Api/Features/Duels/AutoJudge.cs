using Azure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

internal static partial class AutoJudgeLog
{
    [LoggerMessage(EventId = 1210, Level = LogLevel.Information,
        Message = "Auto-judge stood down for duel {DuelId}: {Reason}")]
    public static partial void StoodDown(ILogger logger, string duelId, string reason);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Information,
        Message = "Auto-judge recorded {Verdict} for duel {DuelId}.")]
    public static partial void Recorded(ILogger logger, string duelId, DuelVerdict verdict);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Error, Message = "Auto-judge failed for duel {DuelId}.")]
    public static partial void Failed(ILogger logger, Exception ex, string duelId);
}

/// <summary>
/// Decides a duel that no human judged within the grace window.
/// </summary>
/// <remarks>
/// This reverses the original human-only-verdict rule (PRD §9 item 7) and is why every verdict
/// now carries a <see cref="VerdictSource"/>. Two invariants keep it honest:
/// a human who picks inside the window always wins the race, and a judge that cannot reach a
/// decision leaves the duel Pending rather than guessing — ELO never moves on no evidence.
/// </remarks>
public sealed class AutoJudge
{
    private readonly IDuelRepository _duelRepository;
    private readonly IDuelResultRepository _duelResultRepository;
    private readonly RecordVerdictHandler _recordVerdict;
    private readonly IDuelJudge _judge;
    private readonly IHubContext<DuelHub> _hubContext;
    private readonly AutoJudgeOptions _options;
    private readonly ILogger<AutoJudge> _logger;

    public AutoJudge(
        IDuelRepository duelRepository,
        IDuelResultRepository duelResultRepository,
        RecordVerdictHandler recordVerdict,
        IDuelJudge judge,
        IHubContext<DuelHub> hubContext,
        IOptions<AutoJudgeOptions> options,
        ILogger<AutoJudge> logger)
    {
        _duelRepository = duelRepository;
        _duelResultRepository = duelResultRepository;
        _recordVerdict = recordVerdict;
        _judge = judge;
        _hubContext = hubContext;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Waits out the grace window, then decides the duel if it is still unjudged. Never throws —
    /// a failed auto-judge must leave the duel judgeable by hand, not break duel execution.
    /// </summary>
    public async Task RunAsync(string duelId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_options.DelaySeconds, 0, 3600)), cancellationToken);

            var duel = await _duelRepository.GetByIdAsync(duelId);
            if (duel is null)
            {
                AutoJudgeLog.StoodDown(_logger, duelId, "duel not found");
                return;
            }

            // The human picked inside the window — their decision stands, no second opinion.
            if (duel.Verdict != DuelVerdict.Pending)
            {
                AutoJudgeLog.StoodDown(_logger, duelId, $"already judged ({duel.Verdict})");
                return;
            }

            if (duel.IsExpired)
            {
                AutoJudgeLog.StoodDown(_logger, duelId, "past its verdict deadline");
                return;
            }

            var results = await _duelResultRepository.GetByDuelIdAsync(duelId);
            var left = results.FirstOrDefault(r => r.ModelId == duel.LeftModelId);
            var right = results.FirstOrDefault(r => r.ModelId == duel.RightModelId);

            var decision = await DecideAsync(duel, left, right, cancellationToken);
            if (decision is null) return;

            await RecordAsync(duelId, decision, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down — the duel stays Pending and can still be judged by hand.
        }
        catch (Exception ex)
        {
            AutoJudgeLog.Failed(_logger, ex, duelId);
        }
    }

    private async Task<JudgeDecision?> DecideAsync(
        Duel duel,
        DuelResult? left,
        DuelResult? right,
        CancellationToken cancellationToken)
    {
        var leftOk = left is not null && !left.IsFailure && !string.IsNullOrWhiteSpace(left.HtmlOutputRaw);
        var rightOk = right is not null && !right.IsFailure && !string.IsNullOrWhiteSpace(right.HtmlOutputRaw);

        // Nothing to compare. Leave it Pending — the Arena tells the user to run a fresh duel.
        if (!leftOk && !rightOk)
        {
            AutoJudgeLog.StoodDown(_logger, duel.DuelId, "neither model produced output");
            return null;
        }

        // One side produced nothing. The comparison is settled without spending a judge call,
        // and the rationale records why so an AI-awarded walkover is not mistaken for a
        // considered decision.
        if (leftOk != rightOk)
        {
            var verdict = leftOk ? DuelVerdict.Left : DuelVerdict.Right;
            var failed = leftOk ? right : left;
            var why = string.IsNullOrWhiteSpace(failed?.FailureReason)
                ? "produced no output"
                : failed.FailureReason!.Split('\n', 2)[0].Trim();
            return new JudgeDecision(verdict, $"Awarded by default — the other model {why}.");
        }

        return await _judge.JudgeAsync(
            string.IsNullOrWhiteSpace(duel.PromptFull) ? duel.PromptText : duel.PromptFull,
            left!.HtmlOutputRaw,
            right!.HtmlOutputRaw,
            cancellationToken);
    }

    private async Task RecordAsync(string duelId, JudgeDecision decision, CancellationToken cancellationToken)
    {
        var command = new RecordVerdictCommand(
            duelId,
            decision.Verdict,
            VerdictSource.Ai,
            decision.Rationale,
            _options.Deployment);

        VerdictResponseDto? response;
        try
        {
            try
            {
                response = await _recordVerdict.HandleAsync(command);
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Lost an optimistic-concurrency race (standards §5.5); the handler re-reads
                // everything, so one retry resolves against fresh state.
                response = await _recordVerdict.HandleAsync(command);
            }
        }
        catch (InvalidOperationException ex)
        {
            // A human landed a verdict between our Pending check and this write. Theirs wins.
            AutoJudgeLog.StoodDown(_logger, duelId, ex.Message);
            return;
        }

        if (response is null) return;

        AutoJudgeLog.Recorded(_logger, duelId, decision.Verdict);

        await _hubContext.Clients
            .Group($"duel:{duelId}")
            .SendAsync("VerdictRecorded", response, cancellationToken);
    }
}
