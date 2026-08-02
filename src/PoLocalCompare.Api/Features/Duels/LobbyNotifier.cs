using Microsoft.AspNetCore.SignalR;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

internal static partial class LobbyNotifierLog
{
    [LoggerMessage(EventId = 1230, Level = LogLevel.Debug,
        Message = "Lobby broadcast failed for duel {DuelId}; the ticker will simply miss this event.")]
    public static partial void BroadcastFailed(ILogger logger, Exception ex, DuelId duelId);
}

/// <summary>
/// Publishes duel lifecycle events to the app-wide ticker.
/// </summary>
/// <remarks>
/// Every method swallows its own failures. The ticker is ambient decoration on the nav bar —
/// a SignalR hiccup must never fail a duel, a verdict, or an ELO write, all of which are the
/// actual work. Model names are resolved by the caller, which already holds them.
/// </remarks>
public sealed class LobbyNotifier(IHubContext<DuelHub> hubContext, ILogger<LobbyNotifier> logger)
{
    private const int PromptSummaryLength = 70;

    public Task DuelStartedAsync(
        Duel duel,
        string leftModelName,
        string rightModelName,
        CancellationToken cancellationToken = default) =>
        SendAsync(new LobbyEventDto
        {
            Kind = LobbyEventKind.DuelStarted,
            DuelId = duel.DuelId,
            PromptSummary = Summarize(duel.PromptText),
            LeftModelName = leftModelName,
            RightModelName = rightModelName,
        }, duel.DuelId, cancellationToken);

    public Task DuelCompletedAsync(
        Duel duel,
        string leftModelName,
        string rightModelName,
        CancellationToken cancellationToken = default) =>
        SendAsync(new LobbyEventDto
        {
            Kind = LobbyEventKind.DuelCompleted,
            DuelId = duel.DuelId,
            PromptSummary = Summarize(duel.PromptText),
            LeftModelName = leftModelName,
            RightModelName = rightModelName,
        }, duel.DuelId, cancellationToken);

    public Task VerdictRecordedAsync(
        Duel duel,
        string leftModelName,
        string rightModelName,
        string? winnerModelName,
        VerdictResponseDto response,
        CancellationToken cancellationToken = default) =>
        SendAsync(new LobbyEventDto
        {
            Kind = LobbyEventKind.VerdictRecorded,
            DuelId = duel.DuelId,
            PromptSummary = Summarize(duel.PromptText),
            LeftModelName = leftModelName,
            RightModelName = rightModelName,
            WinnerModelName = winnerModelName,
            Source = response.Source,
            EloShiftWinner = response.EloShiftWinner,
        }, duel.DuelId, cancellationToken);

    private async Task SendAsync(LobbyEventDto payload, DuelId duelId, CancellationToken cancellationToken)
    {
        try
        {
            await hubContext.Clients
                .Group(DuelHub.LobbyGroup)
                .SendAsync("LobbyEvent", payload, cancellationToken);
        }
        catch (Exception ex)
        {
            LobbyNotifierLog.BroadcastFailed(logger, ex, duelId);
        }
    }

    private static string Summarize(string promptText) =>
        promptText.Length <= PromptSummaryLength
            ? promptText
            : promptText[..PromptSummaryLength].TrimEnd() + "…";
}
