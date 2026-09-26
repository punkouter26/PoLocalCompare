using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the confetti burst in <c>wwwroot/js/fx.js</c>.
/// </summary>
/// <remarks>
/// <para>
/// Browser models run WebLLM inference over WebGPU in this same tab, and the tok/s the race
/// reports is measured while that is happening. So the burst checks the GPU lease
/// (<c>window.poGpuLease</c>, held by <c>webllm-interop.js</c> for the life of a worker) and does
/// nothing while it is held. Callers do not gate that, nor reduced motion.
/// </para>
/// <para>
/// <b>The module is imported as an <see cref="IJSObjectReference"/>.</b> Until 2026-09-22
/// every call here was <c>InvokeVoidAsync("import(&quot;/js/fx.js?v=N&quot;).then(m => …)")</c>. Blazor does
/// not evaluate that: it splits the identifier on '.' and walks <c>window</c>, so it looked up
/// <c>window[&quot;import(&quot;/js/fx&quot;]</c>, threw "Could not find", and the catch below swallowed it.
/// No effect in the app had ever run from Blazor. The <c>?v=</c> cache-buster still applies.
/// </para>
/// </remarks>
public sealed class FxService(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "/js/fx.js?v=7";

    private Task<IJSObjectReference>? _module;

    /// <summary>
    /// Fires a burst centred on the first element matching <paramref name="selector"/>, falling
    /// back to the viewport centre when nothing matches.
    /// </summary>
    /// <param name="count">Particle count. The default suits a duel verdict.</param>
    public Task BurstFromAsync(string selector, int count = 90) =>
        CallAsync("burstFrom", selector, new { count });

    /// <summary>A larger burst for a tournament champion — the final should outweigh a duel.</summary>
    public Task ChampionBurstAsync(string selector) => BurstFromAsync(selector, count: 160);

    private async Task CallAsync(string identifier, params object?[] args)
    {
        try
        {
            _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
            var module = await _module;
            await module.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException or JSDisconnectedException)
        {
            // Decoration. A browser without Canvas2D, a failed module fetch, or a component
            // disposed mid-call simply does not celebrate. A failed import is not cached: the
            // next call retries rather than staying dark for the session.
            if (_module is { IsFaulted: true }) _module = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is { IsCompletedSuccessfully: true })
        {
            try
            {
                await _module.Result.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Tab closing.
            }
        }
    }
}
