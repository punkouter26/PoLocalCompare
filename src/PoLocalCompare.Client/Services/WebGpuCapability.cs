using Microsoft.JSInterop;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// Single-flight WebGPU capability probe shared by Home and ModelHealthPanel.
///
/// Without this, every page that mounts a model picker re-requests a GPU adapter and
/// device even when a sister component on the same page has already done so — both
/// round-trips are async, both run on first render, and neither cache. The probe
/// itself costs ~one adapter request + one device request, which is small but
/// not free, and the result never changes during the lifetime of the tab.
/// </summary>
public sealed class WebGpuCapability
{
    private readonly IJSRuntime _js;
    private readonly Lazy<Task<WebGpuInfo?>> _probe;

    public WebGpuCapability(IJSRuntime js)
    {
        _js = js;
        _probe = new Lazy<Task<WebGpuInfo?>>(
            InvokeProbeAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<WebGpuInfo?> GetAsync() => _probe.Value;

    private async Task<WebGpuInfo?> InvokeProbeAsync()
    {
        try
        {
            return await _js.InvokeAsync<WebGpuInfo>("checkWebGpu");
        }
        catch (JSException)
        {
            // diag-interop.js not loaded — treat as unsupported so callers default to
            // a safe path rather than racing on an undefined promise.
            return new WebGpuInfo { Supported = false, Reason = "WebGPU probe unavailable." };
        }
        catch (JSDisconnectedException)
        {
            return new WebGpuInfo { Supported = false, Reason = "Browser disconnected." };
        }
    }

    /// <summary>Minimal shape mirrored from diag-interop.js's checkWebGpu return.</summary>
    public sealed class WebGpuInfo
    {
        public bool Supported { get; set; }
        public string Vendor { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Device { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}