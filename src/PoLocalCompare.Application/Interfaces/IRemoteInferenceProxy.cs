// SOLID: Dependency Inversion
using PoLocalCompare.Domain.Entities;

namespace PoLocalCompare.Application.Interfaces;

/// <summary>
/// Proxy interface for calling remote inference models (Azure AI Foundry).
/// </summary>
public interface IRemoteInferenceProxy
{
    /// <summary>
    /// Streams tokens from the remote model and returns a completed DuelResult.
    /// Reports intermediate status updates via the provided async callback.
    /// </summary>
    Task<DuelResult> RunInferenceAsync(
        Model model,
        string duelId,
        string promptFull,
        Func<int, long, Task> onTokenUpdate,
        CancellationToken cancellationToken);
}
