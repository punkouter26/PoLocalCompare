using PoLocalCompare.Api.Features.Tournaments;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Tournaments;

namespace PoLocalCompare.Unit;

/// <summary>
/// The two rules that decide what a bracket may contain, both changed on 2026-08-23.
/// </summary>
/// <remarks>
/// Pinned here because both are reversals of recorded decisions and the reasoning is easy to
/// re-derive backwards. Browser models were excluded (PRD §9 item 21) on the grounds that a
/// bracket outlives the tab and WebGPU inference does not; they are allowed now because the
/// Tournament page drives those matches itself, and the tab-open requirement moved from a
/// prohibition to a warning on the page. The 4-model bracket was dropped as a strictly worse 8.
/// </remarks>
public class TournamentFieldTests
{
    [Theory]
    [InlineData(ModelType.Remote)]
    [InlineData(ModelType.LocalService)]
    [InlineData(ModelType.Local)]
    public void EveryModelType_MayNowEnterABracket(ModelType modelType)
    {
        Assert.True(CreateTournamentHandler.IsEligible(modelType));
    }

    [Fact]
    public void SupportedSizes_AreTheSinglesMatchAndTheFullBracket()
    {
        Assert.Equal([2, 8], BracketPlanner.SupportedSizes);
    }
}
