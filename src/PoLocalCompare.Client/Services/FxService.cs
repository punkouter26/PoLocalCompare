using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the canvas effects in <c>wwwroot/js/fx.js</c>: the one-shot bursts,
/// the photo-finish strip and the shader backdrop.
/// </summary>
/// <remarks>
/// <para>
/// Browser models run WebLLM inference over WebGPU in this same tab, and the tok/s the race
/// reports is measured while that is happening. So every effect in the module checks the GPU
/// lease (<c>window.poGpuLease</c>, held by <c>webllm-interop.js</c> for the life of a worker)
/// and does nothing while it is held, and the one continuous effect — the backdrop — stops its
/// loop outright rather than idling. Callers do not gate any of that, nor reduced motion.
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
    private const string ModulePath = "/js/fx.js?v=4";

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

    /// <summary>Fires an expanding radial shockwave ripple from an element.</summary>
    public Task ShockwaveAsync(string selector, string? color = null) =>
        color is null ? CallAsync("shockwaveFrom", selector) : CallAsync("shockwaveFrom", selector, new { color });

    /// <summary>Fires a 2.5D directional shard shatter burst from the element.</summary>
    public Task ShardShatterAsync(string selector) => CallAsync("shardShatterFrom", selector);

    /// <summary>Triggers multi-stage 3D tumbling confetti ribbons and golden embers.</summary>
    public Task ChampionPyrotechnicsAsync(string selector) => CallAsync("championPyrotechnicsFrom", selector);

    /// <summary>Transfers glowing kinetic motes from the losing card to the winner's Elo badge.</summary>
    public Task MoteTransferAsync(string fromSelector, string toSelector) =>
        CallAsync("moteTransfer", fromSelector, toSelector);

    /// <summary>
    /// The finish-line camera strip: both sides' tok/s history as slit-scan lanes, with the
    /// margin. The caller states the same margin as text; this is the decorative half.
    /// </summary>
    public Task PhotoFinishAsync(
        string leftName, IReadOnlyList<double> leftHistory,
        string rightName, IReadOnlyList<double> rightHistory,
        string winner, double marginMs) =>
        CallAsync("photoFinish", new
        {
            left = new { name = leftName, history = leftHistory },
            right = new { name = rightName, history = rightHistory },
            winner,
            marginMs,
        });

    /// <summary>Initializes the shader backdrop. A no-op without WebGL2.</summary>
    public Task InitLivingCanvasAsync(string canvasId = "po-living-canvas") =>
        CallAsync("initLivingCanvas", canvasId);

    /// <summary>'idle' or 'victory' — a crowned champion turns the wash gold.</summary>
    public Task SetLivingStateAsync(string state) => CallAsync("setLivingState", state);

    /// <summary>
    /// Tells the backdrop where the app is. It refuses to run on routes where a browser model
    /// could be inferring (/arena, /tournament), whether or not one is right now.
    /// </summary>
    public Task SetLivingRouteAsync(string path) => CallAsync("setLivingRoute", path);

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
