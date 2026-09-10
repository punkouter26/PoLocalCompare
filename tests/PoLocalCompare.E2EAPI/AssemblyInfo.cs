using Xunit;

// Run this assembly's test collections one at a time.
//
// Each collection owns an ApiAppFixture, and each fixture starts its own Azurite container and
// boots the full API — including a SINGLE-consumer BackgroundTaskService that TournamentRunner
// drives a whole bracket through. xUnit runs collections in parallel by default, which pits two
// hosts against each other and lets one collection's tournament hold the queue while the other
// collection's duels wait for results that cannot arrive.
//
// Sequential collections cost a little wall-clock time here and buy a deterministic result,
// which matters because this suite gates the deploy (see .github/workflows/deploy.yml).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
