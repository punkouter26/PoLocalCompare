using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// JS interop wrapper for the synthesised Web Audio cues in <c>wwwroot/js/audio.js</c>:
/// a click tick, a verdict, a tie, the judge window's closing seconds, and an opt-in audible
/// token race. The rest of the old sound design (drone, heartbeat, crowd roar, fanfares…) was
/// cut on 2026-09-26 as decoration nobody asked for; the race and countdown came back on
/// 2026-10-11 on request, the race off by default.
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
    private const string ModulePath = "/js/audio.js?v=8";

    private Task<IJSObjectReference>? _module;

    /// <summary>Verdict recorded — a bright major arpeggio.</summary>
    /// <param name="pan">-1 (left) to 1 (right): the cue comes from the side that won.</param>
    public Task PlaySuccessAsync(double pan = 0) => CallAsync("playSuccess", pan);

    /// <summary>One rising note for each of the judge window's last three seconds.</summary>
    public Task PlayCountdownAsync(int remaining) => CallAsync("playCountdown", remaining);

    /// <summary>
    /// Feeds the audible race each side's current tok/s (0 silences a side). Does nothing
    /// unless the listener has switched race sounds on — the default is off.
    /// </summary>
    public Task RaceUpdateAsync(double leftTps, double rightTps) => CallAsync("raceUpdate", leftTps, rightTps);

    /// <summary>Stops the audible race. It also stops itself two seconds after the last update.</summary>
    public Task RaceStopAsync() => CallAsync("raceStop");

    /// <summary>Whether the opt-in audible race is switched on.</summary>
    public async Task<bool> IsRaceOnAsync()
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<bool>("isRaceOn");
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            ResetIfFaulted();
            return false;
        }
    }

    /// <summary>Sets and persists the audible-race preference. Returns the value applied.</summary>
    public async Task<bool> SetRaceOnAsync(bool on)
    {
        try
        {
            return await (await ModuleAsync()).InvokeAsync<bool>("setRaceOn", on);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            ResetIfFaulted();
            return on;
        }
    }

    /// <summary>A judged draw — deliberately unresolved, neither up nor down.</summary>
    public Task PlayTieAsync() => CallAsync("playTie");

    /// <summary>Short UI tick for a click. Quiet on purpose — it fires often.</summary>
    public Task PlayTickAsync() => CallAsync("playTick");

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
