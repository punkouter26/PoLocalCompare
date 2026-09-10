using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the synthesised Web Audio cues in <c>wwwroot/js/audio.js</c>.
/// </summary>
/// <remarks>
/// Every cue is synthesised at play time — there are no audio assets. The previous version
/// fetched two WAV files that were 44-byte stubs (a RIFF header with a zero-length data chunk),
/// so every "sound" the app played was silence and had been since the cues were added. Nothing
/// here can fail that way again: there is no file to be present-but-empty.
///
/// Every method swallows its own failures. Audio is decoration — a browser with no
/// <c>AudioContext</c>, or a page the user has not yet interacted with (autoplay policy blocks
/// a context until a gesture), must degrade to silence rather than taking a duel down.
///
/// The <c>?v=</c> on the import is the same cache-buster trap the rest of this app's JS carries:
/// without bumping it, a browser that has the old module cached serves the old module and edits
/// here appear to do nothing.
/// </remarks>
public sealed class AudioService(IJSRuntime js)
{
    private const string Module = "'/js/audio.js?v=4'";

    /// <summary>Pre-duel snare roll — accelerating noise hits into an accent.</summary>
    public Task PlaySnareRollAsync() => InvokeAsync("playSnareRoll()");

    /// <summary>Verdict recorded — a bright major arpeggio.</summary>
    public Task PlaySuccessAsync() => InvokeAsync("playSuccess()");

    /// <summary>Tournament champion — longer and wider than a duel verdict, because it is.</summary>
    public Task PlayFanfareAsync() => InvokeAsync("playFanfare()");

    /// <summary>A judged draw — deliberately unresolved, neither up nor down.</summary>
    public Task PlayTieAsync() => InvokeAsync("playTie()");

    /// <summary>A model failed, or a tournament run was abandoned.</summary>
    public Task PlayDefeatAsync() => InvokeAsync("playDefeat()");

    /// <summary>Short UI tick for selection. Quiet on purpose — it fires often.</summary>
    public Task PlayTickAsync() => InvokeAsync("playTick()");

    /// <summary>Swept-noise whoosh for a panel or view change.</summary>
    public Task PlayWhooshAsync() => InvokeAsync("playWhoosh()");

    /// <summary>Quantum ignition sub-bass drop (85 Hz down to 22 Hz).</summary>
    public Task PlaySubDropAsync() => InvokeAsync("playSubDrop()");

    /// <summary>Pre-duel ignition clash with dual panned sweeps and metallic accent.</summary>
    public Task PlayIgnitionClashAsync() => InvokeAsync("playIgnitionClash()");

    /// <summary>Photo-finish supersonic crack and bass boom.</summary>
    public Task PlayShockwaveAsync() => InvokeAsync("playShockwave()");

    /// <summary>AI Judge verdict gavel impact and major chord resolution.</summary>
    public Task PlayGavelImpactAsync() => InvokeAsync("playGavelImpact()");

    /// <summary>Elo rating transfer coin cascade chimes.</summary>
    public Task PlayCoinCascadeAsync() => InvokeAsync("playCoinCascade()");

    /// <summary>Green score resonance pure harmonic chime (528 Hz Solfeggio + fifth).</summary>
    public Task PlayHarmonicChimeAsync() => InvokeAsync("playHarmonicChime()");

    /// <summary>Tactile dual-action mechanical keyboard switch click.</summary>
    public Task PlayMechanicalClickAsync(bool isDown = true) =>
        InvokeAsync($"playMechanicalClick({(isDown ? "true" : "false")})");

    /// <summary>Starts or stops the low-frequency ambient cyber-drone.</summary>
    public Task SetAmbientDroneAsync(bool enabled) =>
        InvokeAsync($"setAmbientDrone({(enabled ? "true" : "false")})");

    /// <summary>
    /// A blip whose pitch tracks generation speed, so the race can be heard as well as seen.
    /// </summary>
    /// <remarks>
    /// Safe to call on every token batch: the module throttles to one blip per 110 ms per side,
    /// which it has to, because batches arrive many times a second on both sides at once. Safe
    /// to call <em>during inference</em> too — this runs on the audio thread and never touches
    /// the WebGPU device WebLLM is generating on, so it cannot skew tok/s or a time budget.
    /// </remarks>
    public Task PlayTokenBlipAsync(double velocity, string side) =>
        InvokeAsync($"playTokenBlip({velocity.ToString(System.Globalization.CultureInfo.InvariantCulture)}, '{side}')");

    /// <summary>Reads the persisted mute preference.</summary>
    public async Task<bool> IsMutedAsync()
    {
        try
        {
            return await js.InvokeAsync<bool>($"import({Module}).then(m => m.isMuted())");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Sets and persists the mute preference. Returns the value actually applied.</summary>
    public async Task<bool> SetMutedAsync(bool muted)
    {
        try
        {
            return await js.InvokeAsync<bool>(
                $"import({Module}).then(m => m.setMuted({(muted ? "true" : "false")}))");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException)
        {
            return muted;
        }
    }

    private async Task InvokeAsync(string call)
    {
        try
        {
            await js.InvokeVoidAsync($"import({Module}).then(m => m.{call})");
        }
        catch (Exception ex) when (ex is JSException or TaskCanceledException or InvalidOperationException)
        {
            // No AudioContext, autoplay not yet unlocked, or the component was disposed
            // mid-call. All three are "no sound", none is an error worth surfacing.
        }
    }
}
