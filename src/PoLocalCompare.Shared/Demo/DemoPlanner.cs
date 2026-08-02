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

    public static IReadOnlyList<DemoRound> Plan(
        IReadOnlyList<ModelId> modelPool,
        int rounds = DefaultRounds,
        int? seed = null)
    {
        if (modelPool.Count < MinimumModels || rounds <= 0)
            return [];

        var random = seed is null ? new Random() : new Random(seed.Value);

        var prompts = Cycle(Shuffle(PromptLibrary.SelfRunning, random), rounds);
        var pairings = Cycle(Shuffle(AllPairs(modelPool), random), rounds);

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
