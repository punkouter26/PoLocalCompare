using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace PoLocalCompare.Client.Services;

internal sealed class InferenceSessionStore
{
    private readonly Dictionary<string, InferenceSession> _sessions = [];

    public InferenceSession CreateOrReplace(string modelId)
    {
        var session = new InferenceSession();
        _sessions[modelId] = session;
        return session;
    }

    public bool TryGet(string modelId, out InferenceSession session) =>
        _sessions.TryGetValue(modelId, out session!);

    public void Clear() => _sessions.Clear();
}

internal sealed class InferenceSession
{
    public Channel<WebLlmStatusUpdate> Channel { get; } =
        System.Threading.Channels.Channel.CreateUnbounded<WebLlmStatusUpdate>();

    [JsonIgnore]
    public TaskCompletionSource<DuelResultPayload> CompletionSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
