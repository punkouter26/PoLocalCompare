using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using PoLocalCompare.Client;
using PoLocalCompare.Client.Services;
using System;

try
{
    var builder = WebAssemblyHostBuilder.CreateDefault(args);
    builder.RootComponents.Add<App>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");

    builder.Services.AddScoped(sp => new HttpClient(new CorrelationHandler { InnerHandler = new HttpClientHandler() })
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress),
    });

    // ─── BFF auth: server owns the session; client only reads /auth/me. No tokens in WASM. ──
    builder.Services.AddAuthorizationCore();
    builder.Services.AddScoped<BffAuthenticationStateProvider>();
    builder.Services.AddScoped<AuthenticationStateProvider>(sp =>
        sp.GetRequiredService<BffAuthenticationStateProvider>());

    // ─── DuelApiClient (T098) ────────────────────────────────────────────────────
    builder.Services.AddScoped<DuelApiClient>();

    // ─── Phase 3 client services ─────────────────────────────────────────────────
    builder.Services.AddScoped<AudioService>();
    builder.Services.AddScoped<ThemeService>();
    builder.Services.AddScoped<FxService>();
    // Scoped, which under a WebAssembly host is one instance for the whole app session: the
    // ticker holds a single lobby SignalR connection that must survive page navigation rather
    // than reconnect on each one. (Singleton is not an option — it depends on HttpClient.)
    builder.Services.AddScoped<LobbyTickerService>();
    builder.Services.AddScoped<WebLlmService>();
    builder.Services.AddTransient<SignalRDuelClient>();

    // Single-flight WebGPU capability probe, so
    // only one adapter/device request is made even when both mount on the same page.
    builder.Services.AddScoped<WebGpuCapability>();

    // Radzen.Blazor services (re-added 2026-09-02). Registers the dialog/notification/tooltip
    // hosts its components expect; the Archive grid and the profile chart both need it present
    // even though neither opens a dialog, because RadzenComponent resolves them in OnInitialized.
    builder.Services.AddRadzenComponents();
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CLIENT_STARTUP_FATAL: {ex.GetType().FullName}: {ex.Message}");
}
