using System.Threading.Channels;

namespace PoLocalCompare.Api.Services;

/// <summary>
/// Background task queue for reliable duel execution (replaces fire-and-forget Task.Run).
/// </summary>
public interface IBackgroundTaskQueue
{
    void QueueBackgroundWork(Func<CancellationToken, Task> workItem);
    Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken);
}

public sealed class BackgroundTaskQueue : IBackgroundTaskQueue
{
    private readonly Channel<Func<CancellationToken, Task>> _queue;

    public BackgroundTaskQueue(int capacity = 100)
    {
        _queue = Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public void QueueBackgroundWork(Func<CancellationToken, Task> workItem)
    {
        if (!_queue.Writer.TryWrite(workItem))
        {
            throw new InvalidOperationException("Background task queue is full.");
        }
    }

    public Task<Func<CancellationToken, Task>> DequeueAsync(CancellationToken cancellationToken) =>
        _queue.Reader.ReadAsync(cancellationToken).AsTask();
}
