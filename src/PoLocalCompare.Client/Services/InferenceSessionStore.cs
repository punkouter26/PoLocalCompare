using System.Text.Json.Serialization;
using System.Threading.Channels;

using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Client.Services;

internal sealed class InferenceSessionStore
{
    private readonly Dictionary<ModelId, InferenceSession> _sessions = [];

    public InferenceSession CreateOrReplace(ModelId modelId)
    {
        var session = new InferenceSession();
        _sessions[modelId] = session;
        return session;
    }

    public bool TryGet(ModelId modelId, out InferenceSession session) =>
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
