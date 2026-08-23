using PoLocalCompare.Shared.Presentation;

namespace PoLocalCompare.Unit;

/// <summary>
/// The Arena and the demo runner both feed this from a SignalR handler that fires per token
/// batch. The contract that matters is that a burst costs one render, that an update arriving
/// after a flush is not swallowed, and that a disposed coalescer never touches a component that
/// has gone away.
/// </summary>
public class RenderCoalescerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(30);

    /// <summary>Long enough for a scheduled flush to have run, short enough to keep the suite fast.</summary>
    private static Task SettleAsync() => Task.Delay(Interval * 6);

    [Fact]
    public async Task ABurstOfRequests_CostsASingleRender()
    {
        var renders = 0;
        using var coalescer = new RenderCoalescer(() => { Interlocked.Increment(ref renders); return Task.CompletedTask; }, Interval);

        for (var i = 0; i < 200; i++) coalescer.Request();
        await SettleAsync();

        Assert.Equal(1, renders);
    }

    [Fact]
    public async Task RequestsInSeparateWindows_EachRender()
    {
        var renders = 0;
        using var coalescer = new RenderCoalescer(() => { Interlocked.Increment(ref renders); return Task.CompletedTask; }, Interval);

        coalescer.Request();
        await SettleAsync();
        coalescer.Request();
        await SettleAsync();

        // The point of the trailing edge: a later update is never folded into an already-
        // completed render, so the last token a model emits still reaches the screen.
        Assert.Equal(2, renders);
    }

    [Fact]
    public async Task FlushAsync_RendersImmediately()
    {
        var renders = 0;
        using var coalescer = new RenderCoalescer(() => { Interlocked.Increment(ref renders); return Task.CompletedTask; }, Interval);

        // Terminal state (a duel finishing) must not wait out an interval.
        await coalescer.FlushAsync();

        Assert.Equal(1, renders);
    }

    [Fact]
    public async Task AfterDispose_RequestNeverRenders()
    {
        var renders = 0;
        var coalescer = new RenderCoalescer(() => { Interlocked.Increment(ref renders); return Task.CompletedTask; }, Interval);

        coalescer.Request();
        coalescer.Dispose();
        await SettleAsync();

        // The component is gone; rendering into it would throw on a disposed dispatcher.
        Assert.Equal(0, renders);

        coalescer.Request();
        await SettleAsync();
        Assert.Equal(0, renders);
    }

    [Fact]
    public async Task ARenderThatThrowsObjectDisposed_DoesNotBringDownTheHandler()
    {
        // Real failure mode: the duel finishes and the Arena is disposed while a coalesced
        // render is still in flight. The exception surfaces on a background task with nobody
        // to catch it, so it has to be swallowed here.
        using var coalescer = new RenderCoalescer(() => throw new ObjectDisposedException("renderer"), Interval);

        coalescer.Request();
        await SettleAsync();

        await coalescer.FlushAsync();
    }
}
