// GoF: Aggregate Root
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public sealed class Duel
{
    public DuelId DuelId { get; init; }
    public string PromptText { get; init; }
    public string PromptFull { get; init; }
    public ModelId LeftModelId { get; init; }
    public ModelId RightModelId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DuelVerdict Verdict { get; set; }
    public ModelId? WinnerModelId { get; set; }
    public ModelId? LoserModelId { get; set; }
    public double? EloShiftWinner { get; set; }
    public double? EloShiftLoser { get; set; }
    /// <summary>Absolute deadline for verdict submission (VerdictDeadlineHours from config).</summary>
    public DateTimeOffset VerdictDeadline { get; init; }

    /// <summary>
    /// Who decided this duel. Defaults to <see cref="VerdictSource.Human"/> so duels recorded
    /// before the auto-judge existed read back as human-judged, which is what they were.
    /// </summary>
    public VerdictSource VerdictSource { get; set; } = VerdictSource.Human;

    /// <summary>The auto-judge's one-line reason, when <see cref="VerdictSource"/> is Ai.</summary>
    public string? JudgeRationale { get; set; }

    /// <summary>Deployment name of the model that judged, when <see cref="VerdictSource"/> is Ai.</summary>
    public string? JudgeModel { get; set; }
    /// <summary>True when only one model completed (partial duel).</summary>
    public bool IsPartial { get; set; }

    /// <summary>Storage concurrency token; set when loaded from Table Storage (standards §5.5).</summary>
    public string? ETag { get; set; }

    public Duel(
        DuelId duelId,
        string promptText,
        string promptFull,
        ModelId leftModelId,
        ModelId rightModelId,
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
        PromptText = string.Empty;
        PromptFull = string.Empty;
    }

    /// <summary>Returns true if the verdict deadline has passed.</summary>
    public bool IsExpired => Verdict == DuelVerdict.Pending && DateTimeOffset.UtcNow > VerdictDeadline;
}