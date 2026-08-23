using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box contract checks on the duel endpoints: status codes, validation shape, and the
/// verdict invariants. Everything here goes over HTTP with no reach into server internals, so
/// these are the tests that would catch a breaking change to the surface the client depends on.
/// </summary>
[Collection("E2EAPI")]
public sealed class DuelContractTests(ApiAppFixture app)
{
    private static async Task<string> RegisterModelAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"{prefix} {Guid.NewGuid():N}",
            ModelType = "Remote",
            ApiEndpointRef = "contract-deployment",
            InputTokenPricePerMillion = 0.10m,
            OutputTokenPricePerMillion = 0.30m,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }

    private static async Task<(string DuelId, string Left, string Right)> CommenceAsync(HttpClient client)
    {
        var left = await RegisterModelAsync(client, "Left");
        var right = await RegisterModelAsync(client, "Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build a single-file HTML kanban board with drag and drop.",
        });
        response.EnsureSuccessStatusCode();
        var duelId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("duelId").GetString()!;
        return (duelId, left, right);
    }

    // ── Commence ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Commence_Returns202WithALocationHeader()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Loc Left");
        var right = await RegisterModelAsync(client, "Loc Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "Build an HTML stopwatch with lap times.",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
    }

    [Fact]
    public async Task Commence_EchoesTheSubmittedFieldAndStartsPending()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, right) = await CommenceAsync(client);

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");

        Assert.Equal(left, duel.GetProperty("leftModelId").GetString());
        Assert.Equal(right, duel.GetProperty("rightModelId").GetString());
        Assert.Contains("kanban", duel.GetProperty("promptText").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Pending", duel.GetProperty("verdict").GetString());
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadRequest)]                    // empty prompt
    [InlineData("Build an HTML calculator.", HttpStatusCode.NotFound)] // unknown opponent
    public async Task Commence_RejectsTheRequestWhenValidationOrCatalogFails(
        string promptText, HttpStatusCode expectedStatus)
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Left For Reject");
        var right = await RegisterModelAsync(client, "Right For Reject");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = promptText == string.Empty ? right : "01NOTAREALMODELID00000000",
            PromptText = promptText,
        });

        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task Commence_SameModelBothSides_IsRejected()
    {
        using var client = app.CreateAuthenticatedClient();
        var only = await RegisterModelAsync(client, "Solo");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = only,
            RightModelId = only,
            PromptText = "Build an HTML calculator.",
        });

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Commence_Anonymous_PassesTheOpenGate_ButFailsOnMissingModel()
    {
        // Features:AllowAnonymousWrites opens the gate for un-authenticated callers in dev/test.
        // The fixture sets the flag on, so anon posts pass auth and reach the handler — which
        // then fails on the missing model with 404. Tests that care about the response from a
        // fully-valid request should use CreateAuthenticatedClient().
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = "01AAAAAAAAAAAAAAAAAAAAAAAA",
            RightModelId = "01BBBBBBBBBBBBBBBBBBBBBBBB",
            PromptText = "Build an HTML calculator.",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Read ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDuel_UnknownId_Returns404()
    {
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/duels/01NOTAREALDUELIDAAAAAAAAAA");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ListDuels_ReturnsTheCommencedDuel()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        var duels = await client.GetFromJsonAsync<JsonElement[]>("/api/duels?limit=100");

        Assert.Contains(duels!, d => d.GetProperty("duelId").GetString() == duelId);
    }

    [Fact]
    public async Task ListDuels_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();

        var response = await client.GetAsync("/api/duels");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Verdict ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verdict_MovesEloInOppositeDirections()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        var response = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(body.GetProperty("eloShiftWinner").GetDouble() > 0);
        Assert.True(body.GetProperty("eloShiftLoser").GetDouble() < 0);
    }

    [Fact]
    public async Task Verdict_NamesTheSidesAndIsRecordedAsHuman()
    {
        // Standards invariant: every verdict carries a source, and one submitted over the
        // verdict endpoint is by definition a person's. The winner / loser ids are the
        // dominant the human picked, mirrored into the response so the client can refresh.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, right) = await CommenceAsync(client);

        var response = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Right" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(right, body.GetProperty("winnerModelId").GetString());
        Assert.Equal(left, body.GetProperty("loserModelId").GetString());

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");
        Assert.Equal("Human", duel.GetProperty("verdictSource").GetString());
    }

    [Fact]
    public async Task Verdict_Twice_Returns409()
    {
        // ELO must move exactly once per duel; a second verdict would double-count it.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });
        var second = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Right" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Verdict_UnknownDuel_Returns404()
    {
        using var client = app.CreateAuthenticatedClient();

        var response = await client.PostAsJsonAsync(
            "/api/duels/01NOTAREALDUELIDAAAAAAAAAA/verdict", new { Verdict = "Left" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Verdict_Anonymous_PassesTheOpenGate_ButFailsOnMissingDuel()
    {
        // The verdict endpoint is opened by Features:AllowAnonymousWrites in dev/test, so an
        // anon call surfaces the underlying 404 (duel not found) rather than a 401.
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/duels/01AAAAAAAAAAAAAAAAAAAAAAAA/verdict", new { Verdict = "Left" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Local (browser) result ingest ──────────────────────────────────────

[Fact]
    public async Task LocalResult_WithoutAModelId_Returns400()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        var response = await client.PostAsJsonAsync($"/api/duels/{duelId}/local-result", new
        {
            ModelId = "",
            HtmlOutputRaw = "<html></html>",
            TokenCount = 1,
            TotalDurationMs = 10L,
            WarmUpDurationMs = 1L,
            IsFailure = false,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalResult_AppearsOnTheDuelAndIsNormalizedBeforeStorage()
    {
        // Browser-inference path: the server never saw the tokens, the client POSTs the
        // finished output. It has to converge with the server-side path — and the markdown
        // fence has to be stripped so the browser model is scored on the same basis.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, _) = await CommenceAsync(client);

        await client.PostAsJsonAsync($"/api/duels/{duelId}/local-result", new
        {
            ModelId = left,
            HtmlOutputRaw = "```html\n<html><body>Local</body></html>\n```",
            TokenCount = 55,
            TotalDurationMs = 900L,
            WarmUpDurationMs = 100L,
            IsFailure = false,
        });

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");
        var result = duel.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("modelId").GetString() == left);

        Assert.DoesNotContain("```", result.GetProperty("htmlOutputRaw").GetString());
    }

    [Fact]
    public async Task LocalResult_Anonymous_IsAlwaysAllowed()
    {
        // The browser WebLLM worker posts without an auth cookie, so /local-result is always
        // anonymous — the duel's owner is whoever created the duel, not whoever posts the
        // HTML back. The endpoint returns 400 here because ModelId is empty; the salient
        // assertion is that the response is not 401.
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/duels/01AAAAAAAAAAAAAAAAAAAAAAAA/local-result", new { ModelId = "" });

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Archive export ─────────────────────────────────────────────────────

    [Fact]
    public async Task Report_ForAJudgedDuel_ReturnsSelfContainedHtml()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);
        await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });

        var response = await client.GetAsync($"/api/duels/{duelId}/report");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("<html", html, StringComparison.OrdinalIgnoreCase);
    }
}
