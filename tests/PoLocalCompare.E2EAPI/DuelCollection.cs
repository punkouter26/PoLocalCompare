using Xunit;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Its own collection — and therefore its own <see cref="ApiAppFixture"/>, host and background
/// queue — so the duel contract tests cannot be starved by the tournament tests.
/// </summary>
/// <remarks>
/// <para>
/// Every E2EAPI class used to share one collection and one host. That host has a SINGLE
/// <c>BackgroundTaskService</c> consumer (by design — see CLAUDE.md), and <c>TournamentRunner</c>
/// drives a whole bracket through it. So a tournament match that does not reach a verdict does
/// not merely stall the bracket: it holds the only queue there is, and every duel a later test
/// commences sits behind it and never executes.
/// </para>
/// <para>
/// That is exactly what happened once the duel tests started waiting for their result rows
/// instead of racing them. Running the assembly as a whole gave
/// <c>Verdict_Twice_Returns409</c> and <c>Verdict_NamesTheSidesAndIsRecordedAsHuman</c> 20-second
/// timeouts, while <c>dotnet test --filter "FullyQualifiedName!~TournamentContractTests"</c>
/// passed 19/19 in two seconds. Splitting the collection is what removes the coupling rather
/// than papering over it with a longer timeout.
/// </para>
/// <para>
/// The cost is a second Azurite container and a second host for this assembly. That is a fair
/// price for a deterministic suite, and it is only paid by the E2E tier, which does not gate the
/// deploy anyway.
/// </para>
/// </remarks>
[CollectionDefinition(DuelCollection.Name)]
public sealed class DuelCollection : ICollectionFixture<DuelApiFixture>
{
    public const string Name = "E2EAPI-Duels";
}

/// <summary>
/// The duel collection's host: same API, but the auto-judge is off.
/// </summary>
/// <remarks>
/// No tournament runs in this collection, so nothing needs a judge to advance — and leaving it on
/// would let a grace window sleep on the single-consumer queue that these tests themselves need.
/// </remarks>
public sealed class DuelApiFixture : ApiAppFixture
{
    protected override bool EnableAutoJudge => false;
}
