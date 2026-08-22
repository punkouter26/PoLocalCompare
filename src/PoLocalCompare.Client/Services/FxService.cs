using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the one-shot particle bursts in <c>wwwroot/js/fx.js</c>.
/// </summary>
/// <remarks>
/// Canvas2D, one-shot, and fired only at moments when inference has already finished — a
/// verdict landing, a champion being crowned. That restraint is not a style preference.
/// Browser models run WebLLM inference over WebGPU in this same tab, and two things depend on
/// that GPU being free: the tok/s the race reports, and whether a model comes in under a
/// <c>MaxSeconds</c> challenge budget. A budget miss forfeits the duel and moves ELO, so a
/// persistent render loop competing for the GPU would not just look bad — it would record
/// wrong verdicts. Continuous motion elsewhere in the app is done on the CSS compositor
/// instead, which does not contend the same way.
///
/// The module itself no-ops under <c>prefers-reduced-motion</c> and caps concurrent bursts, so
/// callers do not have to gate either.
/// </remarks>
public sealed class FxService(IJSRuntime js)
{
    private const string Module = "'/js/fx.js?v=1'";

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
}
