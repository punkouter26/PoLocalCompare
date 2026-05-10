using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoLocalCompare.Client;
using PoLocalCompare.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// ─── DuelApiClient (T098) ────────────────────────────────────────────────────
builder.Services.AddScoped<DuelApiClient>();

// ─── Phase 3 client services ─────────────────────────────────────────────────
builder.Services.AddScoped<AudioService>();
builder.Services.AddScoped<WebLlmService>();
builder.Services.AddTransient<SignalRDuelClient>();

await builder.Build().RunAsync();
