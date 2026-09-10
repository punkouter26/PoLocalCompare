using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the one-shot particle bursts in <c>wwwroot/js/fx.js</c>.
/// </summary>
/// <remarks>
/// Canvas2D, one-shot, and fired only at moments when inference has already finished — a
/// verdict landing, a champion being crowned. That restraint is not a style preference.
/// Browser models run WebLLM inference over WebGPU in this same tab, and the tok/s the race
/// reports is measured while that is happening. A persistent render loop competing for the GPU
/// would not merely drop frames — it would make the number the app exists to report wrong, and
/// slow a browser model's own generation while it is being timed. Continuous motion elsewhere
/// in the app is done on the CSS compositor instead, which does not contend the same way.
///
/// The module itself no-ops under <c>prefers-reduced-motion</c> and caps concurrent bursts, so
/// callers do not have to gate either.
/// </remarks>
public sealed class FxService(IJSRuntime js)
{
    private const string Module = "'/js/fx.js?v=3'";

    /// <summary>
    /// Fires a burst centred on the first element matching <paramref name="selector"/>, falling
    /// back to the viewport centre when nothing matches.
    /// </summary>
    /// <param name="count">Particle count. The default suits a duel verdict.</param>
    public async Task BurstFromAsync(string selector, int count = 90)
    {
        try
        {
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.burstFrom('{selector}', {{ count: {count} }}))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException)
        {
            // Decoration. A browser without Canvas2D, or a component disposed mid-call, simply
            // does not celebrate.
        }
    }

    /// <summary>A larger burst for a tournament champion — the final should outweigh a duel.</summary>
    public Task ChampionBurstAsync(string selector) => BurstFromAsync(selector, count: 160);

    /// <summary>Fires an expanding radial shockwave ripple from an element.</summary>
    public async Task ShockwaveAsync(string selector, string? color = null)
    {
        try
        {
            var colorParam = color is not null ? $", color: '{color}'" : "";
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.shockwaveFrom('{selector}'{colorParam}))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException) { }
    }

    /// <summary>Fires a 2.5D directional shard shatter burst from the element.</summary>
    public async Task ShardShatterAsync(string selector)
    {
        try
        {
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.shardShatterFrom('{selector}'))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException) { }
    }

    /// <summary>Triggers multi-stage 3D tumbling confetti ribbons and golden embers.</summary>
    public async Task ChampionPyrotechnicsAsync(string selector)
    {
        try
        {
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.championPyrotechnicsFrom('{selector}'))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException) { }
    }

    /// <summary>Transfers glowing kinetic motes from the losing card to the winner's Elo badge.</summary>
    public async Task MoteTransferAsync(string fromSelector, string toSelector)
    {
        try
        {
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.moteTransfer('{fromSelector}', '{toSelector}'))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException) { }
    }

    /// <summary>Initializes the living atmosphere background canvas.</summary>
    public async Task InitLivingCanvasAsync(string canvasId = "po-living-canvas")
    {
        try
        {
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.initLivingCanvas('{canvasId}'))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException) { }
    }

    /// <summary>Updates the living atmosphere state ('idle', 'battle', 'victory').</summary>
    public async Task SetLivingStateAsync(string state)
    {
        try
        {
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.setLivingState('{state}'))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException) { }
    }

    /// <summary>
    /// Critical GPU Invariant: Pauses background canvas during local WebLLM inference
    /// to safeguard 100% of GPU compute pipelines.
    /// </summary>
    public async Task PauseLivingAsync(bool paused)
    {
        try
        {
            await js.InvokeVoidAsync(
                $"import({Module}).then(m => m.pauseLiving({(paused ? "true" : "false")}))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException) { }
    }
}

