// GoF: Aggregate Root
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class Duel
{
    public string DuelId { get; init; }
    public string PromptText { get; init; }
    public string PromptFull { get; init; }
    public string LeftModelId { get; init; }
    public string RightModelId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DuelVerdict Verdict { get; set; }
    public string? WinnerModelId { get; set; }
    public string? LoserModelId { get; set; }
    public double? EloShiftWinner { get; set; }
    public double? EloShiftLoser { get; set; }
    /// <summary>Absolute deadline for verdict submission (VerdictDeadlineHours from config).</summary>
    public DateTimeOffset VerdictDeadline { get; init; }
    /// <summary>True when only one model completed (partial duel).</summary>
    public bool IsPartial { get; set; }

    /// <summary>Storage concurrency token; set when loaded from Table Storage (standards §5.5).</summary>
    public string? ETag { get; set; }

    public Duel(
        string duelId,
        string promptText,
        string promptFull,
        string leftModelId,
        string rightModelId,
        int verdictDeadlineHours = 24)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("PromptText cannot be empty.", nameof(promptText));

        if (leftModelId == rightModelId)
            throw new ArgumentException("LeftModelId and RightModelId must differ — a model cannot duel itself.");

        DuelId = duelId;
        PromptText = promptText;
        PromptFull = promptFull;
        LeftModelId = leftModelId;
        RightModelId = rightModelId;
        StartedAt = DateTimeOffset.UtcNow;
        VerdictDeadline = StartedAt.AddHours(verdictDeadlineHours);
        Verdict = DuelVerdict.Pending;
    }

    // Parameterless constructor for Azure Table Storage deserialization
    public Duel()
    {
        DuelId = string.Empty;
        PromptText = string.Empty;
        PromptFull = string.Empty;
        LeftModelId = string.Empty;
        RightModelId = string.Empty;
    }

    /// <summary>Returns true if the verdict deadline has passed.</summary>
    public bool IsExpired => Verdict == DuelVerdict.Pending && DateTimeOffset.UtcNow > VerdictDeadline;
}