using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Prompts;

namespace PoLocalCompare.Shared.Demo;

/// <summary>One scheduled demo duel, resolved before the run starts so the queue is visible up front.</summary>
public sealed record DemoRound(
    int Index,
    PromptTemplate Prompt,
    ModelId LeftModelId,
    ModelId RightModelId);

/// <summary>
/// Chooses the pairings and prompts for a demo run.
/// </summary>
/// <remarks>
/// Pure and seeded, so the whole schedule is decided — and unit-testable — before a single duel
/// is enqueued. That matters because demo duels are real duels: they persist, they are judged,
/// and they move ELO, so "what is about to run" has to be inspectable rather than emergent.
/// Callers pass a pool already filtered to <see cref="Enums.ModelType.Remote"/>; browser models
/// need WebGPU and a foreground tab, which an unattended demo cannot assume.
/// </remarks>
public static class DemoPlanner
{
    public const int DefaultRounds = 10;

    /// <summary>Minimum pool size — a model cannot duel itself.</summary>
    public const int MinimumModels = 2;

    /// <summary>
    /// Plans a demo run from a pool of model ids. Two ids with the same display name (e.g. two
    /// table rows both seeded as "Phi-4 Mini" with different capitalisation in their endpoint
    /// ref) will produce a pair that reads as "Phi-4 Mini vs Phi-4 Mini" on the Arena, which is
    /// indistinguishable from a self-duel and which judges unhelpfully. Pass <paramref name="nameResolver"/>
    /// to filter duplicates up front; the id-only overload is kept for tests where the pool is
    /// already known to be distinct.
    /// </summary>
    public static IReadOnlyList<DemoRound> Plan(
        IReadOnlyList<ModelId> modelPool,
        int rounds = DefaultRounds,
        int? seed = null,
        Func<ModelId, string?>? nameResolver = null)
    {
        var pool = nameResolver is null ? modelPool : DedupeByName(modelPool, nameResolver);

        if (pool.Count < MinimumModels || rounds <= 0)
            return [];

        var random = seed is null ? new Random() : new Random(seed.Value);

        var prompts = Cycle(Shuffle(PromptLibrary.SelfRunning, random), rounds);
        var pairings = Cycle(Shuffle(AllPairs(pool), random), rounds);

        var plan = new List<DemoRound>(rounds);
        for (var i = 0; i < rounds; i++)
        {
            var (a, b) = pairings[i];

            // Which model gets the left panel is its own coin flip. Without it the pair ordering
            // from AllPairs would put the same model on the left every time it appeared, and any
            // left/right bias in the judge would read as a bias about that model.
            var swap = random.Next(2) == 0;

            plan.Add(new DemoRound(
                Index: i,
                Prompt: prompts[i],
                LeftModelId: swap ? b : a,
                RightModelId: swap ? a : b));
        }

        return plan;
    }

    /// <summary>
    /// Drops models whose display name (case-insensitive, trimmed) collides with one already
    /// kept in the output. First wins, preserving insertion order from the source pool so the
    /// deterministic seed in tests stays meaningful. Exposed publicly so callers can report
    /// the post-dedupe pool size without re-running the dedupe themselves.
    /// </summary>
    public static List<ModelId> DedupeByName(
        IReadOnlyList<ModelId> pool,
        Func<ModelId, string?> nameResolver)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModelId>(pool.Count);

        foreach (var id in pool)
        {
            var name = nameResolver(id);
            var key = string.IsNullOrWhiteSpace(name) ? id.Value : name.Trim();
            if (seen.Add(key)) result.Add(id);
        }

        return result;
    }

    /// <summary>Every unordered pair in the pool, so a run spreads across matchups before repeating one.</summary>
    private static List<(ModelId A, ModelId B)> AllPairs(IReadOnlyList<ModelId> pool)
    {
        var pairs = new List<(ModelId, ModelId)>();
        for (var i = 0; i < pool.Count; i++)
        {
            for (var j = i + 1; j < pool.Count; j++)
            {
                pairs.Add((pool[i], pool[j]));
            }
        }
        return pairs;
    }

    private static List<T> Shuffle<T>(IReadOnlyList<T> source, Random random)
    {
        var items = source.ToList();
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
        return items;
    }

    /// <summary>
    /// Repeats the source until <paramref name="count"/> items exist, reshuffling each time it
    /// wraps so a second lap is not a replay of the first.
    /// </summary>
    private static List<T> Cycle<T>(List<T> source, int count)
    {
        var output = new List<T>(count);
        var index = 0;

        while (output.Count < count)
        {
            output.Add(source[index]);
            index++;

            if (index == source.Count) index = 0;
        }

        return output;
    }
}
