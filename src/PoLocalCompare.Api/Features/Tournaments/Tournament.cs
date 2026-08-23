// GoF: Aggregate Root
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Api.Features.Tournaments;

/// <summary>One match position inside a persisted bracket.</summary>
/// <remarks>
/// Mutable, unlike <see cref="BracketMatch"/>: the planner's record is the pure shape of a
/// bracket, and this is that shape plus what happened when it was played. They are kept apart
/// so the seeding and advancement rules stay unit-testable without a storage account.
/// </remarks>
public sealed class TournamentMatch
{
    public int Round { get; set; }
    public int Index { get; set; }

    public ModelId SlotAModelId { get; set; }
    public string SlotAName { get; set; } = string.Empty;

    /// <summary>1-based seed, carried with the model as it advances. See <see cref="BracketSlot"/>.</summary>
    public int SlotASeed { get; set; }

    public ModelId SlotBModelId { get; set; }
    public string SlotBName { get; set; } = string.Empty;

    /// <inheritdoc cref="SlotASeed"/>
    public int SlotBSeed { get; set; }

    /// <summary>The duel this match ran as. Null until the runner starts it.</summary>
    public DuelId? DuelId { get; set; }

    public ModelId? WinnerModelId { get; set; }
    public string? WinnerName { get; set; }
    public int WinnerSeed { get; set; }

    /// <summary>
    /// Set when the AI judge returned a draw and the better seed advanced on the tie-break,
    /// so the bracket can say so rather than presenting it as a won match.
    /// </summary>
    public bool WonOnSeedTieBreak { get; set; }

    /// <summary>Why this match could not be decided. Set only on a terminal failure.</summary>
    public string? FailureReason { get; set; }

    public bool IsReady => !SlotAModelId.IsEmpty && !SlotBModelId.IsEmpty;
    public bool IsDecided => WinnerModelId is not null;
}

/// <summary>
/// A single-elimination bracket run: one prompt, a seeded field, and the duels that decide it.
/// </summary>
/// <remarks>
/// Persisted as one row with the matches serialised into a single column rather than as a row
/// per match. A bracket is at most seven matches and is only ever read whole, so a multi-row
/// aggregate would buy nothing but the risk of a half-written bracket.
///
/// Bracket matches are ordinary duels — they persist, they are judged, and they move ELO,
/// exactly as demo mode's do. That is deliberate: a tournament that did not count would be a
/// simulation of the thing rather than the thing.
/// </remarks>
public sealed class Tournament
{
    public TournamentId TournamentId { get; init; }

    /// <summary>2, 4 or 8 — validated through <see cref="BracketPlanner.IsSupportedSize"/>.</summary>
    public int Size { get; init; }

    /// <summary>
    /// The prompt every match in the bracket receives, as typed. Stored once rather than per
    /// match — a bracket compares models, so varying the task between rounds would make the
    /// winner a function of the draw. The CDN suffix each duel actually sends is appended by
    /// <see cref="CommenceDuelHandler"/>; keeping a second copy here would let the two drift.
    /// </summary>
    public string PromptText { get; init; } = string.Empty;

    public TournamentStatus Status { get; set; } = TournamentStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Forensic only, never used for authorization — same rule as <c>Duel.OwnerId</c>.</summary>
    public string? OwnerId { get; set; }

    public ModelId? ChampionModelId { get; set; }
    public string? ChampionName { get; set; }

    /// <summary>Why the run stopped short. Set together with <see cref="TournamentStatus.Abandoned"/>.</summary>
    public string? AbandonedReason { get; set; }

    public List<TournamentMatch> Matches { get; set; } = [];

    /// <summary>Storage concurrency token; set when loaded from Table Storage (standards §5.5).</summary>
    public string? ETag { get; set; }

    public Tournament() { }

    /// <summary>
    /// Draws a bracket. The field must already be strongest-first — seeding is by ELO, and this
    /// type does not know about ratings.
    /// </summary>
    public static Tournament Draw(
        TournamentId tournamentId,
        int size,
        IReadOnlyList<BracketSlot> seededField,
        string promptText,
        string? ownerId)
    {
        if (!BracketPlanner.IsSupportedSize(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Bracket size must be 2, 4 or 8.");

        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("A tournament needs a prompt.", nameof(promptText));

        var bracket = BracketPlanner.Build(seededField, size);

        return new Tournament
        {
            TournamentId = tournamentId,
            Size = size,
            PromptText = promptText,
            CreatedAt = DateTimeOffset.UtcNow,
            OwnerId = ownerId,
            Status = TournamentStatus.Pending,
            Matches = bracket.Select(m => new TournamentMatch
            {
                Round = m.Round,
                Index = m.Index,
                SlotAModelId = m.SlotA.ModelId,
                SlotAName = m.SlotA.DisplayName,
                SlotASeed = m.SlotA.Seed,
                SlotBModelId = m.SlotB.ModelId,
                SlotBName = m.SlotB.DisplayName,
                SlotBSeed = m.SlotB.Seed,
            }).ToList(),
        };
    }

    /// <summary>
    /// The next match to run: the earliest undecided one with both contestants known. Null when
    /// the bracket is finished or is waiting on a result that has not landed yet.
    /// </summary>
    public TournamentMatch? NextPlayable() => AllPlayable().FirstOrDefault();

    /// <summary>
    /// Every match that could be played right now, in bracket order.
    /// </summary>
    /// <remarks>
    /// Within a round these are genuinely independent — round 1's four quarter-finals share no
    /// state and neither depends on another's result — so the runner can have several in flight
    /// at once instead of waiting out each in turn. Rounds still gate on each other, and that
    /// falls out of <c>IsReady</c> rather than needing a check here: a semi-final has no
    /// contestants until both its feeder matches have decided, so it simply is not playable yet.
    /// </remarks>
    public IEnumerable<TournamentMatch> AllPlayable() =>
        Matches
            .Where(m => !m.IsDecided && m.FailureReason is null && m.IsReady)
            .OrderBy(m => m.Round)
            .ThenBy(m => m.Index);

    /// <summary>
    /// Records a winner and moves it into the next round. Returns false when the match is
    /// unknown or already decided, so a replayed result cannot advance the bracket twice.
    /// </summary>
    public bool RecordWinner(
        int round,
        int index,
        ModelId winnerModelId,
        string winnerName,
        bool wonOnSeedTieBreak = false)
    {
        var match = Matches.FirstOrDefault(m => m.Round == round && m.Index == index);
        if (match is null || match.IsDecided) return false;

        var winnerSeed = winnerModelId == match.SlotAModelId ? match.SlotASeed : match.SlotBSeed;

        match.WinnerModelId = winnerModelId;
        match.WinnerName = winnerName;
        match.WinnerSeed = winnerSeed;
        match.WonOnSeedTieBreak = wonOnSeedTieBreak;

        var target = BracketPlanner.NextSlot(round, index, Size);
        if (target is var (nextRound, nextIndex, intoSlotA))
        {
            var next = Matches.FirstOrDefault(m => m.Round == nextRound && m.Index == nextIndex);
            if (next is not null)
            {
                if (intoSlotA)
                {
                    next.SlotAModelId = winnerModelId;
                    next.SlotAName = winnerName;
                    next.SlotASeed = winnerSeed;
                }
                else
                {
                    next.SlotBModelId = winnerModelId;
                    next.SlotBName = winnerName;
                    next.SlotBSeed = winnerSeed;
                }
            }
        }
        else
        {
            // No next slot means this was the final.
            ChampionModelId = winnerModelId;
            ChampionName = winnerName;
            Status = TournamentStatus.Complete;
            CompletedAt = DateTimeOffset.UtcNow;
        }

        return true;
    }

    /// <summary>
    /// Which contestant a drawn match awards. The better seed advances — an ordinary tournament
    /// tie-break, and the only resolution available that does not invent an opinion about which
    /// output was better. The alternative is abandoning a whole bracket over one draw.
    /// </summary>
    public static (ModelId ModelId, string Name) SeedTieBreak(TournamentMatch match) =>
        match.SlotASeed <= match.SlotBSeed
            ? (match.SlotAModelId, match.SlotAName)
            : (match.SlotBModelId, match.SlotBName);

    /// <summary>
    /// Stops the run without a champion. A bracket cannot skip a match — every later round is
    /// seeded by the winner of an earlier one — so an undecidable match ends the tournament
    /// rather than being written off as a bye.
    /// </summary>
    public void Abandon(string reason)
    {
        if (Status is TournamentStatus.Complete or TournamentStatus.Abandoned) return;

        Status = TournamentStatus.Abandoned;
        AbandonedReason = reason;
        CompletedAt = DateTimeOffset.UtcNow;
    }

    public TournamentDto ToDto() => new()
    {
        TournamentId = TournamentId,
        Status = Status,
        Size = Size,
        PromptText = PromptText,
        CreatedAt = CreatedAt,
        CompletedAt = CompletedAt,
        OwnerId = OwnerId,
        ChampionModelId = ChampionModelId,
        ChampionName = ChampionName,
        AbandonedReason = AbandonedReason,
        Matches = Matches
            .OrderBy(m => m.Round)
            .ThenBy(m => m.Index)
            .Select(m => new TournamentMatchDto
            {
                Round = m.Round,
                Index = m.Index,
                RoundName = BracketPlanner.RoundName(m.Round, Size),
                SlotAModelId = m.SlotAModelId,
                SlotAName = m.SlotAName,
                SlotASeed = m.SlotASeed,
                SlotBModelId = m.SlotBModelId,
                SlotBName = m.SlotBName,
                SlotBSeed = m.SlotBSeed,
                DuelId = m.DuelId,
                WinnerModelId = m.WinnerModelId,
                WinnerName = m.WinnerName,
                WonOnSeedTieBreak = m.WonOnSeedTieBreak,
                FailureReason = m.FailureReason,
            })
            .ToList(),
    };
}
