using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PoLocalCompare.Client;
using PoLocalCompare.Client.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// ─── Microsoft OAuth via MSAL ─────────────────────────────────────────────────
builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes.Add("openid");
    options.ProviderOptions.DefaultAccessTokenScopes.Add("profile");
    options.ProviderOptions.DefaultAccessTokenScopes.Add("email");
    options.ProviderOptions.LoginMode = "redirect";
});

// ─── Guest authentication (standards §6) ─────────────────────────────────────
builder.Services.AddScoped<GuestAuthService>();

// ─── DuelApiClient (T098) ────────────────────────────────────────────────────
builder.Services.AddScoped<DuelApiClient>();

// ─── Phase 3 client services ─────────────────────────────────────────────────
builder.Services.AddScoped<AudioService>();
builder.Services.AddScoped<WebLlmService>();
builder.Services.AddTransient<SignalRDuelClient>();

await builder.Build().RunAsync();
