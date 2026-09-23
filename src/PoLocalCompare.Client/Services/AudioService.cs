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
/// It failed a second way after that, just as silently: until 2026-09-22 every call was
/// <c>InvokeVoidAsync("import(&quot;/js/audio.js?v=N&quot;).then(m => …)")</c>, which Blazor does not
/// evaluate — it walks <c>window</c> for an identifier split on '.', throws "Could not find",
/// and the catch below swallowed it. No cue ever played and the mute toggle never persisted.
/// The module is now imported once as an <see cref="IJSObjectReference"/>.
///
/// Every method swallows its own failures. Audio is decoration — a browser with no
/// <c>AudioContext</c>, or a page the user has not yet interacted with (autoplay policy blocks
/// a context until a gesture), must degrade to silence rather than taking a duel down.
///
/// The <c>?v=</c> on the import is the same cache-buster trap the rest of this app's JS carries:
/// without bumping it, a browser that has the old module cached serves the old module and edits
/// here appear to do nothing.
/// </remarks>
public sealed class AudioService(IJSRuntime js) : IAsyncDisposable
{
    private const string ModulePath = "/js/audio.js?v=5";

    private Task<IJSObjectReference>? _module;

    /// <summary>Pre-duel snare roll — accelerating noise hits into an accent.</summary>
    public Task PlaySnareRollAsync() => CallAsync("playSnareRoll");

    /// <summary>Verdict recorded — a bright major arpeggio.</summary>
    public Task PlaySuccessAsync() => CallAsync("playSuccess");

    /// <summary>Tournament champion — longer and wider than a duel verdict, because it is.</summary>
    public Task PlayFanfareAsync() => CallAsync("playFanfare");

    /// <summary>A judged draw — deliberately unresolved, neither up nor down.</summary>
    public Task PlayTieAsync() => CallAsync("playTie");

    /// <summary>A model failed, or a tournament run was abandoned.</summary>
    public Task PlayDefeatAsync() => CallAsync("playDefeat");

    /// <summary>Short UI tick for selection. Quiet on purpose — it fires often.</summary>
    public Task PlayTickAsync() => CallAsync("playTick");

    /// <summary>Swept-noise whoosh for a panel or view change.</summary>
    public Task PlayWhooshAsync() => CallAsync("playWhoosh");

    /// <summary>Quantum ignition sub-bass drop (85 Hz down to 22 Hz).</summary>
    public Task PlaySubDropAsync() => CallAsync("playSubDrop");

    /// <summary>Pre-duel ignition clash with dual panned sweeps and metallic accent.</summary>
    public Task PlayIgnitionClashAsync() => CallAsync("playIgnitionClash");

    /// <summary>Photo-finish supersonic crack and bass boom.</summary>
    public Task PlayShockwaveAsync() => CallAsync("playShockwave");

    /// <summary>AI Judge verdict gavel impact and major chord resolution.</summary>
    public Task PlayGavelImpactAsync() => CallAsync("playGavelImpact");

    /// <summary>Elo rating transfer coin cascade chimes.</summary>
    public Task PlayCoinCascadeAsync() => CallAsync("playCoinCascade");

    /// <summary>Green score resonance pure harmonic chime (528 Hz Solfeggio + fifth).</summary>
    public Task PlayHarmonicChimeAsync() => CallAsync("playHarmonicChime");

    /// <summary>Tactile dual-action mechanical keyboard switch click.</summary>
    public Task PlayMechanicalClickAsync(bool isDown = true) => CallAsync("playMechanicalClick", isDown);

    /// <summary>Starts or stops the low ambient drone that sits under a live duel.</summary>
    public Task SetAmbientDroneAsync(bool enabled) => CallAsync("setAmbientDrone", enabled);

    /// <summary>
    /// Gives each side of the duel its own musical voice, derived from the model id so a model
    /// always sounds the same. Call once per duel, before the first token blip.
    /// </summary>
    public Task SetDuetVoicesAsync(string leftSeed, string rightSeed) =>
        CallAsync("setDuetVoices", leftSeed, rightSeed);

    /// <summary>
    /// One note of a side's melody. Faster generation plays more notes, higher.
    /// </summary>
    /// <remarks>
    /// Safe to call on every token batch: the module throttles per side, which it has to,
    /// because batches arrive many times a second on both sides at once. Safe to call
    /// <em>during inference</em> too — this runs on the audio thread and never touches the
    /// WebGPU device WebLLM is generating on, so it cannot skew tok/s.
    /// </remarks>
    public Task PlayTokenBlipAsync(double velocity, string side) => CallAsync("playTokenBlip", velocity, side);

    /// <summary>One judge-countdown heartbeat; <paramref name="urgency"/> runs 0 → 1 as time runs out.</summary>
    public Task PlayHeartbeatAsync(double urgency) => CallAsync("playHeartbeat", urgency);

    /// <summary>The rising scan under the judge's reticle.</summary>
    public Task PlayScanSweepAsync() => CallAsync("playScanSweep");

    /// <summary>A leaderboard rank change since the viewer last looked; positive is up.</summary>
    public Task PlayRankShiftAsync(int direction) => CallAsync("playRankShift", direction);

    /// <summary>A bracket winner advancing to the next round.</summary>
    public Task PlayAdvanceAsync() => CallAsync("playAdvance");

    /// <summary>A bracket loser dropping out.</summary>
    public Task PlayKnockoutAsync() => CallAsync("playKnockout");

    /// <summary>Reads the persisted mute preference.</summary>
    public async Task<bool> IsMutedAsync()
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<bool>("isMuted");
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            ResetIfFaulted();
            return false;
        }
    }

    /// <summary>Sets and persists the mute preference. Returns the value actually applied.</summary>
    public async Task<bool> SetMutedAsync(bool muted)
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<bool>("setMuted", muted);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            ResetIfFaulted();
            return muted;
        }
    }

    private Task<IJSObjectReference> ModuleAsync() =>
        _module ??= js.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();

    private async Task CallAsync(string identifier, params object?[] args)
    {
        try
        {
            await (await ModuleAsync()).InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            // No AudioContext, autoplay not yet unlocked, or the component was disposed
            // mid-call. All three are "no sound", none is an error worth surfacing.
            ResetIfFaulted();
        }
    }

    private static bool IsBenign(Exception ex) =>
        ex is JSException or TaskCanceledException or InvalidOperationException or JSDisconnectedException;

    /// <summary>A failed import is retried on the next call rather than cached for the session.</summary>
    private void ResetIfFaulted()
    {
        if (_module is { IsFaulted: true }) _module = null;
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
