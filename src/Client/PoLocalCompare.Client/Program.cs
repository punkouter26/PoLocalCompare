using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.AspNetCore.Components.Authorization;
using PoLocalCompare.Client;
using PoLocalCompare.Client.Services;
using System.Security.Claims;
using System;

try
{
    var builder = WebAssemblyHostBuilder.CreateDefault(args);
    builder.RootComponents.Add<App>("#app");
    builder.RootComponents.Add<HeadOutlet>("head::after");

    builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

    // Register auth services. Use either MSAL or anonymous fallback, never both.
    builder.Services.AddAuthorizationCore();
    var authConfigured = false;

    // ─── Microsoft OAuth via MSAL ─────────────────────────────────────────────────
    var authority = builder.Configuration["AzureAd:Authority"];
    var clientId = builder.Configuration["AzureAd:ClientId"];
    if (!string.IsNullOrWhiteSpace(authority) && !string.IsNullOrWhiteSpace(clientId))
    {
        try
        {
            builder.Services.AddMsalAuthentication(options =>
            {
                builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
                options.ProviderOptions.DefaultAccessTokenScopes.Add("openid");
                options.ProviderOptions.DefaultAccessTokenScopes.Add("profile");
                options.ProviderOptions.DefaultAccessTokenScopes.Add("email");
                options.ProviderOptions.LoginMode = "redirect";
            });

            authConfigured = true;
        }
        catch
        {
            // Keep anonymous auth provider fallback active when MSAL setup fails.
        }
    }

    if (!authConfigured)
    {
        builder.Services.AddScoped<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();
    }

    // ─── Guest authentication (standards §6) ─────────────────────────────────────
    builder.Services.AddScoped<GuestAuthService>();

    // ─── DuelApiClient (T098) ────────────────────────────────────────────────────
    builder.Services.AddScoped<DuelApiClient>();

    // ─── Phase 3 client services ─────────────────────────────────────────────────
    builder.Services.AddScoped<AudioService>();
    builder.Services.AddScoped<WebLlmService>();
    builder.Services.AddTransient<SignalRDuelClient>();    builder.Services.AddTransient<SignalRLobbyClient>();
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"CLIENT_STARTUP_FATAL: {ex.GetType().FullName}: {ex.Message}");
}

file sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly AuthenticationState AnonymousState =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
        => Task.FromResult(AnonymousState);
}
