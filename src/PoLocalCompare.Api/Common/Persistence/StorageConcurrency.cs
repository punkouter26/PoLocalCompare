namespace PoLocalCompare.Api.Common.Persistence;

/// <summary>
/// How many storage reads a single request may have in flight at once.
/// </summary>
/// <remarks>
/// Handlers that fan out over a collection (an archive page, the model roster) must bound
/// themselves: a duel-result read can trigger a blob download for any output over 64 KB, so an
/// unbounded <c>Task.WhenAll</c> over a 100-item page turns one request into hundreds of
/// concurrent calls — enough to exhaust the connection pool and turn a throttled response into a
/// failed page. Bounded fan-out keeps the latency win over serial reads without that tail.
/// </remarks>
internal static class StorageConcurrency
{
    internal const int MaxParallelReads = 8;

    /// <summary>
    /// Runs <paramref name="read"/> for each index in <paramref name="count"/>, at most
    /// <see cref="MaxParallelReads"/> at a time, returning results in the original order.
    /// </summary>
    internal static async Task<T[]> ReadAllAsync<T>(int count, Func<int, Task<T>> read)
    {
        var results = new T[count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads },
            async (index, _) => results[index] = await read(index));
        return results;
    }
}
