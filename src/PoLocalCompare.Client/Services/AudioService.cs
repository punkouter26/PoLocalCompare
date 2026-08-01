using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>JS interop wrapper for Web Audio API cues.</summary>
public sealed class AudioService
{
    private readonly IJSRuntime _js;

    public AudioService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task PlaySnareRollAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("import('/js/audio.js').then(m => m.playSnareRoll())");
        }
        catch (JSException)
        {
            // Audio unavailable — graceful no-op
        }
        catch (TaskCanceledException)
        {
            // Component disposed — ignore
        }
    }

    public async Task PlaySuccessAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("import('/js/audio.js').then(m => m.playSuccess())");
        }
        catch (JSException)
        {
            // Audio unavailable — graceful no-op
        }
        catch (TaskCanceledException)
        {
            // Component disposed — ignore
        }
    }
}
