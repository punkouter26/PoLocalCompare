using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.Tournaments;

/// <summary>One slot in a bracket: filled from seeding, or later by a winner.</summary>
/// <param name="Seed">
/// 1-based seed number, stamped by <see cref="BracketPlanner.Build"/> and carried with the model
/// as it advances. Zero for an empty slot. It is what makes a drawn match resolvable without
/// inventing anything: a tie advances the better seed, which is an ordinary tournament rule
/// rather than a guess about which output was better.
/// </param>
public sealed record BracketSlot(ModelId ModelId, string DisplayName, int Seed = 0)
{
    public bool IsEmpty => ModelId.IsEmpty;

    public static BracketSlot Empty { get; } = new(default, string.Empty, 0);
}

/// <summary>A single match position in the bracket, addressed by round and index.</summary>
/// <param name="Round">0 = the first round played, counting up toward the final.</param>
/// <param name="Index">Position within the round, 0-based, top of the bracket first.</param>
public sealed record BracketMatch(
    int Round,
    int Index,
    BracketSlot SlotA,
    BracketSlot SlotB)
{
    /// <summary>Both contestants known, so this match can actually be run.</summary>
    public bool IsReady => !SlotA.IsEmpty && !SlotB.IsEmpty;
}

/// <summary>
/// Lays out a single-elimination bracket and works out where each winner goes next.
/// </summary>
/// <remarks>
/// Pure and separate from the Tournaments slice so the whole shape of a run is decidable — and
/// unit-testable — before anything is written, because bracket matches are real duels that
/// persist and move ELO.
///
/// Seeding is standard tournament seeding rather than a shuffle: the top two seeds are placed so
/// they can only meet in the final. A random draw would routinely knock the two best models out
/// against each other in round one, which produces a winner that says nothing.
/// </remarks>
public static class BracketPlanner
{
    /// <summary>
    /// Bracket sizes the app offers. 2 is a plain 1v1 run through the same machinery; 8 is the
    /// real bracket. 4 was removed on 2026-08-23 — it was a strictly worse 8 (a semi-final pair
    /// tells you less than a quarter-final round) and the middle option nobody reached for.
    /// The maths below is size-generic, so re-adding it is a one-line change.
    /// </summary>
    public static IReadOnlyList<int> SupportedSizes { get; } = [2, 8];

    public static bool IsSupportedSize(int size) => SupportedSizes.Contains(size);

    /// <summary>Number of rounds a bracket of this size takes — 2 → 1, 8 → 3.</summary>
    public static int RoundCount(int size)
    {
        if (!IsSupportedSize(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Bracket size must be 2, 4 or 8.");

        var rounds = 0;
        for (var remaining = size; remaining > 1; remaining /= 2) rounds++;
        return rounds;
    }

    /// <summary>
    /// Human name for a round, counting back from the final so the last round is always "Final"
    /// regardless of bracket size.
    /// </summary>
    public static string RoundName(int round, int size)
    {
        var fromEnd = RoundCount(size) - 1 - round;
        return fromEnd switch
        {
            0 => "Final",
            1 => "Semi-finals",
            2 => "Quarter-finals",
            _ => $"Round {round + 1}",
        };
    }

    /// <summary>
    /// Standard seeding order: the sequence of seed numbers, top of the bracket first, such that
    /// seeds 1 and 2 can only meet in the final.
    /// </summary>
    /// <remarks>
    /// Built by repeated reflection — each round doubles the field, and a seed <c>s</c> in a
    /// bracket of <c>n</c> is paired with <c>n + 1 - s</c>. For 8 this yields
    /// <c>1,8,4,5,2,7,3,6</c>: the classic layout where the top seed meets the bottom one first.
    /// </remarks>
    public static IReadOnlyList<int> SeedOrder(int size)
    {
        if (!IsSupportedSize(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Bracket size must be 2, 4 or 8.");

        var seeds = new List<int> { 1, 2 };

        while (seeds.Count < size)
        {
            var field = seeds.Count * 2;
            var next = new List<int>(field);
            foreach (var seed in seeds)
            {
                next.Add(seed);
                next.Add(field + 1 - seed);
            }
            seeds = next;
        }

        return seeds;
    }

    /// <summary>
    /// Builds the full bracket. Round 0 is seeded from <paramref name="contenders"/>; every later
    /// round is laid out with empty slots for the winners to land in.
    /// </summary>
    /// <param name="contenders">
    /// Strongest first — the caller sorts, because "strongest" is a leaderboard question and this
    /// type does not know about ELO. Must hold exactly <paramref name="size"/> entries.
    /// </param>
    public static IReadOnlyList<BracketMatch> Build(IReadOnlyList<BracketSlot> contenders, int size)
    {
        if (!IsSupportedSize(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "Bracket size must be 2, 4 or 8.");

        if (contenders.Count != size)
            throw new ArgumentException($"A bracket of {size} needs exactly {size} contenders, got {contenders.Count}.", nameof(contenders));

        var order = SeedOrder(size);
        var matches = new List<BracketMatch>();

        // Round 0 — seeded pairs, taken two at a time from the seed order.
        for (var i = 0; i < size / 2; i++)
        {
            // Seed numbers are 1-based; the contenders list is strongest-first, so seed n is at
            // index n - 1. The seed is stamped on here rather than expected from the caller —
            // "strongest first" is the caller's contract, and the number that follows from it
            // is this type's to assign.
            var seedA = order[i * 2];
            var seedB = order[i * 2 + 1];

            matches.Add(new BracketMatch(
                Round: 0,
                Index: i,
                SlotA: contenders[seedA - 1] with { Seed = seedA },
                SlotB: contenders[seedB - 1] with { Seed = seedB }));
        }

        // Later rounds — placeholders that Advance fills in as results land.
        var rounds = RoundCount(size);
        for (var round = 1; round < rounds; round++)
        {
            var inRound = size / (1 << (round + 1));
            for (var i = 0; i < inRound; i++)
                matches.Add(new BracketMatch(round, i, BracketSlot.Empty, BracketSlot.Empty));
        }

        return matches;
    }

    /// <summary>
    /// Where the winner of a match goes. Returns null for the final, which feeds nothing.
    /// </summary>
    /// <remarks>
    /// Two matches feed each match in the next round, so match <c>i</c> advances to match
    /// <c>i / 2</c> — into slot A when <c>i</c> is even and slot B when it is odd. That parity is
    /// what keeps the bracket readable: the winner of the top match stays on top.
    /// </remarks>
    public static (int Round, int Index, bool IntoSlotA)? NextSlot(int round, int index, int size)
    {
        if (round >= RoundCount(size) - 1) return null;
        return (round + 1, index / 2, index % 2 == 0);
    }

    /// <summary>
    /// Places a winner into its next slot and returns the updated bracket. A win in the final
    /// changes nothing structurally — the tournament is simply over.
    /// </summary>
    public static IReadOnlyList<BracketMatch> Advance(
        IReadOnlyList<BracketMatch> bracket,
        int round,
        int index,
        BracketSlot winner,
        int size)
    {
        var target = NextSlot(round, index, size);
        if (target is not var (nextRound, nextIndex, intoSlotA)) return bracket;

        return bracket.Select(m =>
            m.Round == nextRound && m.Index == nextIndex
                ? m with
                {
                    SlotA = intoSlotA ? winner : m.SlotA,
                    SlotB = intoSlotA ? m.SlotB : winner,
                }
                : m).ToList();
    }
}
