using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text.Json;

namespace PoLocalCompare.Api.Endpoints;

/// <summary>
/// Dev/test-only endpoints that support E2E test setup.
/// Registered exclusively in non-Production environments.
/// </summary>
internal static class E2EEndpoints
{
    private const string GuestIdentity = "GUEST_E2E_TEST";

    internal static IEndpointRouteBuilder MapE2EEndpoints(this IEndpointRouteBuilder app)
    {
        // Sets localStorage guest_identity and redirects to the requested path.
        // Replaces page.addInitScript() in Playwright tests with an explicit, debuggable URL.
        app.MapGet("/e2e/seed-auth", (string? redirect) =>
        {
            var safeRedirect = string.IsNullOrWhiteSpace(redirect) ? "/" : redirect;
            // Reject absolute URLs to prevent open redirect
            if (!Uri.IsWellFormedUriString(safeRedirect, UriKind.Relative))
                safeRedirect = "/";

            var identityJson = JsonSerializer.Serialize(GuestIdentity);
            var redirectJson = JsonSerializer.Serialize(safeRedirect);
            var html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body><script>"
                + "try { localStorage.setItem('guest_identity'," + identityJson + "); } catch(e) {}"
                + "location.replace(" + redirectJson + ");"
                + "</script></body></html>";

            return Results.Content(html, "text/html");
        }).ExcludeFromDescription(); // hide from OpenAPI

        return app;
    }
}
