using PoLocalCompare.Shared.Demo;
using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Prompts;

namespace PoLocalCompare.Unit;

/// <summary>
/// Demo duels are ordinary duels — persisted, judged, and moving ELO — so what the planner emits
/// is what actually gets written. The invariants here are the ones that would otherwise corrupt
/// the leaderboard: no model duelling itself, and no left/right bias baked into the schedule.
/// </summary>
public class DemoPlannerTests
{
    private static List<ModelId> Pool(int count) =>
        Enumerable.Range(0, count).Select(i => ModelId.From($"model-{i:D2}")).ToList();

    [Fact]
    public void Plan_FillsTheRequestedNumberOfRounds_IndexedFromZeroInOrder()
    {
        var plan = DemoPlanner.Plan(Pool(4), rounds: 10, seed: 1);

        Assert.Equal(10, plan.Count);
        Assert.Equal(Enumerable.Range(0, 10), plan.Select(r => r.Index));

        // And the schedule still fills when more rounds are asked for than there are prompts,
        // rather than stopping at the end of the library.
        var overflowRounds = PromptLibrary.SelfRunning.Count * 2 + 3;
        var overflowPlan = DemoPlanner.Plan(Pool(4), overflowRounds, seed: 9);

        Assert.Equal(overflowRounds, overflowPlan.Count);
        Assert.Equal(Enumerable.Range(0, overflowRounds), overflowPlan.Select(r => r.Index));
    }

    [Fact]
    public void Plan_NeverPairsAModelWithItself()
    {
        // Duel's constructor throws on this, so a planner that emitted it would fail at round N
        // rather than at plan time.
        var plan = DemoPlanner.Plan(Pool(3), rounds: 25, seed: 7);

        Assert.All(plan, round => Assert.NotEqual(round.LeftModelId, round.RightModelId));
    }

    [Fact]
    public void Plan_IsReproducibleForASeedAndVariesAcrossSeeds()
    {
        var first = DemoPlanner.Plan(Pool(5), rounds: 10, seed: 42);
        var second = DemoPlanner.Plan(Pool(5), rounds: 10, seed: 42);
        var different = DemoPlanner.Plan(Pool(5), rounds: 10, seed: 43);

        static IEnumerable<(string, ModelId, ModelId)> Shape(IEnumerable<DemoRound> plan) =>
            plan.Select(r => (r.Prompt.Id, r.LeftModelId, r.RightModelId));

        Assert.Equal(Shape(first), Shape(second));
        Assert.NotEqual(Shape(first), Shape(different));
    }

    [Fact]
    public void Plan_OnlyUsesSelfRunningPrompts()
    {
        // An unattended screen showing a form nobody types into demonstrates nothing.
        var plan = DemoPlanner.Plan(Pool(4), rounds: 12, seed: 3);

        Assert.All(plan, round => Assert.True(round.Prompt.SelfRunning));
    }

    [Fact]
    public void Plan_SpreadsAcrossMatchupsBeforeRepeatingOne()
    {
        // Six models give fifteen distinct pairs, so ten rounds should never repeat one.
        var plan = DemoPlanner.Plan(Pool(6), rounds: 10, seed: 11);

        var pairs = plan
            .Select(r => r.LeftModelId.CompareTo(r.RightModelId) < 0
                ? (r.LeftModelId, r.RightModelId)
                : (r.RightModelId, r.LeftModelId))
            .ToList();

        Assert.Equal(pairs.Count, pairs.Distinct().Count());
    }

    [Fact]
    public void Plan_DoesNotAlwaysPutTheSameModelOnTheLeft()
    {
        // Any left/right bias in the judge would otherwise read as a bias about that model.
        var plan = DemoPlanner.Plan(Pool(2), rounds: 20, seed: 5);
        var first = Pool(2)[0];

        Assert.Contains(plan, r => r.LeftModelId == first);
        Assert.Contains(plan, r => r.RightModelId == first);
    }

    [Theory]
    [InlineData(0, 10)]   // no models to pair
    [InlineData(1, 10)]   // one model cannot duel itself
    [InlineData(4, 0)]    // nothing asked for
    public void Plan_WithNothingToSchedule_ReturnsNothing(int modelCount, int rounds)
    {
        Assert.Empty(DemoPlanner.Plan(Pool(modelCount), rounds, seed: 1));
    }

    [Fact]
    public void Plan_DropsDuplicatesByCaseInsensitiveName_FirstWins()
    {
        // Regression: Table Storage once held two rows whose DisplayName was "Phi-4" /
        // "phi-4" (different ids, different casing). The Arena rendered "Phi-4 vs phi-4"
        // because the planner only checked ModelId equality. "First wins" lets the canonical
        // row keep its slot in the schedule when the name resolver returns two case variants.
        var pool = new List<ModelId>
        {
            ModelId.From("first-kept"),
            ModelId.From("duplicate-later"),
        };
        var names = new Dictionary<string, string>
        {
            ["first-kept"]      = "Phi-4",
            ["duplicate-later"] = "phi-4",   // same name, different casing
        };

        var plan = DemoPlanner.Plan(
            pool,
            rounds: 10,
            seed: 1,
            nameResolver: id => names.TryGetValue(id.Value, out var n) ? n : null);

        // Only one model was kept, so the planner has nothing to pair and returns empty.
        Assert.Empty(plan);
    }
}
