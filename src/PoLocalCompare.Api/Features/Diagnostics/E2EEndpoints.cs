using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
        // Establishes a guest BFF session cookie and redirects to the requested path.
        // Delegates to the canonical /auth/login/fake route so E2E tests exercise the real flow.
        app.MapGet("/e2e/seed-auth", (string? redirect) =>
        {
            var safeRedirect = string.IsNullOrWhiteSpace(redirect) || !Uri.IsWellFormedUriString(redirect, UriKind.Relative)
                ? "/"
                : redirect;
            return Results.Redirect($"/auth/login/fake?user={GuestIdentity}&returnUrl={Uri.EscapeDataString(safeRedirect)}");
        }).ExcludeFromDescription(); // hide from OpenAPI

        return app;
    }
}
