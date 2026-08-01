using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Tests.E2E.Api;

/// <summary>
/// Black-box checks on the BFF auth gate: protected endpoints reject anonymous callers,
/// public endpoints stay open, and /auth/me reports the session state.
/// </summary>
[Collection("E2EAPI")]
public sealed class AuthGateTests(ApiAppFixture app)
{
    [Fact]
    public async Task Leaderboard_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();
        var response = await client.GetAsync("/api/leaderboard");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Leaderboard_Authenticated_Returns200()
    {
        using var client = app.CreateAuthenticatedClient();
        var response = await client.GetAsync("/api/leaderboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_Anonymous_IsPublic()
    {
        using var client = app.CreateAnonymousClient();
        var response = await client.GetAsync("/health");
        // Health is intentionally unauthenticated (200 healthy or 503 degraded — never 401).
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthMe_Anonymous_ReportsNotAuthenticated()
    {
        using var client = app.CreateAnonymousClient();
        var me = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        Assert.False(me.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task AuthMe_FakeUser_ReportsAuthenticatedIdentity()
    {
        using var client = app.CreateAuthenticatedClient(user: "alice", roles: "User");
        var me = await client.GetFromJsonAsync<JsonElement>("/auth/me");
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("alice", me.GetProperty("name").GetString());
    }
}
