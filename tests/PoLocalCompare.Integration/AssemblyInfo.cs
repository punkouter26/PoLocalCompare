using Xunit;

// Tests within the same [Collection] run sequentially; AzuriteFixture manages the shared container.
[assembly: CollectionBehavior(DisableTestParallelization = false)]
