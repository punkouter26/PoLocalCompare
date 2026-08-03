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
    public void Plan_ProducesTheRequestedNumberOfRounds()
    {
        var plan = DemoPlanner.Plan(Pool(4), rounds: 10, seed: 1);

        Assert.Equal(10, plan.Count);
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
    public void Plan_IndexesRoundsFromZeroInOrder()
    {
        var plan = DemoPlanner.Plan(Pool(3), rounds: 6, seed: 2);

        Assert.Equal(Enumerable.Range(0, 6), plan.Select(r => r.Index));
    }

    [Fact]
    public void Plan_WithTheSameSeed_IsReproducible()
    {
        var first = DemoPlanner.Plan(Pool(5), rounds: 10, seed: 42);
        var second = DemoPlanner.Plan(Pool(5), rounds: 10, seed: 42);

        Assert.Equal(
            first.Select(r => (r.Prompt.Id, r.LeftModelId, r.RightModelId)),
            second.Select(r => (r.Prompt.Id, r.LeftModelId, r.RightModelId)));
    }

    [Fact]
    public void Plan_WithDifferentSeeds_Differs()
    {
        var first = DemoPlanner.Plan(Pool(5), rounds: 10, seed: 1);
        var second = DemoPlanner.Plan(Pool(5), rounds: 10, seed: 2);

        Assert.NotEqual(
            first.Select(r => (r.Prompt.Id, r.LeftModelId, r.RightModelId)),
            second.Select(r => (r.Prompt.Id, r.LeftModelId, r.RightModelId)));
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
    [InlineData(0)]
    [InlineData(1)]
    public void Plan_WithTooFewModels_ReturnsNothing(int modelCount)
    {
        Assert.Empty(DemoPlanner.Plan(Pool(modelCount), rounds: 10, seed: 1));
    }

    [Fact]
    public void Plan_WithNonPositiveRounds_ReturnsNothing()
    {
        Assert.Empty(DemoPlanner.Plan(Pool(4), rounds: 0, seed: 1));
    }

    [Fact]
    public void Plan_RequestingMoreRoundsThanPrompts_StillFillsTheSchedule()
    {
        var rounds = PromptLibrary.SelfRunning.Count * 2 + 3;

        var plan = DemoPlanner.Plan(Pool(4), rounds, seed: 9);

        Assert.Equal(rounds, plan.Count);
    }

    [Fact]
    public void Plan_NeverPairsTwoModelsWithTheSameDisplayName()
    {
        // Regression: Table Storage ended up with two rows whose DisplayName was "Phi-4"
        // (different ids, different apiEndpointRef capitalisation). The Arena rendered
        // "Phi-4 vs Phi-4" because the planner only checked ModelId equality, not name. With
        // a name resolver, the duplicate is dropped before pairing.
        var pool = new List<ModelId>
        {
            ModelId.From("model-A"),
            ModelId.From("model-B"),
            ModelId.From("model-C"),
            ModelId.From("model-D"),   // duplicate of A
            ModelId.From("model-E"),   // duplicate of B (case-insensitive)
        };
        var names = new Dictionary<string, string>
        {
            ["model-A"] = "Phi-4",
            ["model-B"] = "Phi-4 Mini",
            ["model-C"] = "GPT-5 Nano",
            ["model-D"] = "Phi-4",
            ["model-E"] = "PHI-4 MINI",   // case-insensitive duplicate
        };

        var plan = DemoPlanner.Plan(
            pool,
            rounds: 20,
            seed: 7,
            nameResolver: id => names.TryGetValue(id.Value, out var n) ? n : null);

        Assert.NotEmpty(plan);
        Assert.All(plan, round =>
        {
            var leftName = names[round.LeftModelId.Value];
            var rightName = names[round.RightModelId.Value];
            Assert.NotEqual(leftName, rightName);
        });
    }

    [Fact]
    public void Plan_DropsDuplicatesByCaseInsensitiveName_FirstWins()
    {
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
