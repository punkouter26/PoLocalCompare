using Microsoft.JSInterop;
using PoLocalCompare.Client.Services;
using PoLocalCompare.Shared.DTOs;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PoLocalCompare.Client.Services;

/// <summary>JS interop wrapper that starts webllm-worker.js and streams status updates.
/// Supports concurrent dual-model inference by keying sessions by modelId.</summary>
public sealed class WebLlmService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly DuelApiClient _apiClient;

    // Per-modelId session state
    private readonly Dictionary<string, InferenceSession> _sessions = [];
    private DotNetObjectReference<WebLlmService>? _selfRef;

    public WebLlmService(IJSRuntime js, DuelApiClient apiClient)
    {
        _js = js;
        _apiClient = apiClient;
    }

    /// <summary>Starts inference via the WebLLM Web Worker and reports status updates.
    /// Multiple modelIds can run concurrently.</summary>
    public async IAsyncEnumerable<WebLlmStatusUpdate> StartInferenceAsync(
        string modelId,
        string prompt,
        string duelId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _selfRef ??= DotNetObjectReference.Create(this);

        var session = new InferenceSession();
        _sessions[modelId] = session;

        await _js.InvokeVoidAsync("startWebLlmInference", _selfRef, modelId, prompt, cancellationToken);

        await foreach (var update in session.Channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return update;

            if (update.Status is "Done" or "Failed")
            {
                session.Channel.Writer.TryComplete();
                break;
            }
        }
    }

    /// <summary>Returns the full result payload (including HTML) after inference completes.</summary>
    public Task<DuelResultPayload?> GetResultAsync(string modelId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryGetValue(modelId, out var session))
            return session.CompletionSource.Task.WaitAsync(cancellationToken)
                .ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
        return Task.FromResult<DuelResultPayload?>(null);
    }

    [JSInvokable]
    public void ReceiveStatusUpdate(string modelId, string status, int tokenCount, long elapsedMs)
    {
        if (_sessions.TryGetValue(modelId, out var session))
            session.Channel.Writer.TryWrite(new WebLlmStatusUpdate(status, tokenCount, elapsedMs, null));
    }

    [JSInvokable]
    public void ReceiveComplete(string modelId, string htmlOutput, int tokenCount, long totalMs, long warmUpMs)
    {
        if (_sessions.TryGetValue(modelId, out var session))
        {
            session.Channel.Writer.TryWrite(new WebLlmStatusUpdate("Done", tokenCount, totalMs, null));
            session.CompletionSource.TrySetResult(new DuelResultPayload(htmlOutput, tokenCount, totalMs, warmUpMs));
        }
    }

    [JSInvokable]
    public void ReceiveError(string modelId, string reason)
    {
        if (_sessions.TryGetValue(modelId, out var session))
        {
            session.Channel.Writer.TryWrite(new WebLlmStatusUpdate("Failed", 0, 0, reason));
            session.CompletionSource.TrySetException(new InvalidOperationException(reason));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _selfRef?.Dispose();
        _selfRef = null;
        _sessions.Clear();
        await ValueTask.CompletedTask;
    }

    private sealed class InferenceSession
    {
        public Channel<WebLlmStatusUpdate> Channel { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<WebLlmStatusUpdate>();
        public TaskCompletionSource<DuelResultPayload> CompletionSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed record WebLlmStatusUpdate(string Status, int TokenCount, long ElapsedMs, string? ErrorReason);
public sealed record DuelResultPayload(string HtmlOutput, int TokenCount, long TotalMs, long WarmUpMs);
