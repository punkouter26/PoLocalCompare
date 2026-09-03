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

    /// <summary>
    /// A browser (WebLLM) model — the only kind allowed to post its own result, since it is the
    /// only kind that runs in the client. <c>/local-result</c> checks the type, so the local-result
    /// tests need one of these rather than the Remote default above.
    /// </summary>
    private static async Task<string> RegisterLocalModelAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"{prefix} {Guid.NewGuid():N}",
            ModelType = "Local",
            TdpWatts = 45.0,
            WebLlmModelId = "contract-webllm-model",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }

    private static async Task<(string DuelId, string Left, string Right)> CommenceAsync(HttpClient client)
        => await CommenceAsync(client, leftIsLocal: false);

    private static async Task<(string DuelId, string Left, string Right)> CommenceAsync(
        HttpClient client,
        bool leftIsLocal)
    {
        var left = leftIsLocal
            ? await RegisterLocalModelAsync(client, "Left")
            : await RegisterModelAsync(client, "Left");
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

    private static Task<HttpResponseMessage> PostLocalResultAsync(
        HttpClient client,
        string duelId,
        string modelId,
        string html = "<html><body>Local</body></html>") =>
        client.PostAsJsonAsync($"/api/duels/{duelId}/local-result", new
        {
            ModelId = modelId,
            HtmlOutputRaw = html,
            TokenCount = 55,
            TotalDurationMs = 900L,
            WarmUpDurationMs = 100L,
            IsFailure = false,
        });

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
        var (duelId, left, _) = await CommenceAsync(client, leftIsLocal: true);

        await PostLocalResultAsync(client, duelId, left, "```html\n<html><body>Local</body></html>\n```");

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");
        var result = duel.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("modelId").GetString() == left);

        Assert.DoesNotContain("```", result.GetProperty("htmlOutputRaw").GetString());
    }

    [Fact]
    public async Task LocalResult_ForAModelNotInTheDuel_Returns400()
    {
        // The (duelId, modelId) pair is the storage key. Unchecked, a caller picks both and can
        // write a result row into any duel for any model — which is what DuelExecutionService
        // hands to the judge, so it decides duels the caller is not part of.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client, leftIsLocal: true);
        var outsider = await RegisterLocalModelAsync(client, "Outsider");

        var response = await PostLocalResultAsync(client, duelId, outsider);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalResult_ForAServerSideModel_Returns400()
    {
        // Only browser models run in the client, so only they may report their own output. A
        // Remote/Ollama model's result must come from the server that actually produced it.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, right) = await CommenceAsync(client, leftIsLocal: true);

        var response = await PostLocalResultAsync(client, duelId, right);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LocalResult_PostedTwice_Returns409()
    {
        // The repository upserts with Replace, so a second post used to silently overwrite the
        // output the duel is being judged on — including after the fact, rewriting the archive.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, _) = await CommenceAsync(client, leftIsLocal: true);

        var first = await PostLocalResultAsync(client, duelId, left);
        var second = await PostLocalResultAsync(client, duelId, left, "<html><body>Forged</body></html>");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task LocalResult_ForAnUnknownDuel_Returns404()
    {
        // It carried an unconditional AllowAnonymous() and never loaded the duel at all, so any
        // caller could mint rows under a duel id of their choosing.
        using var client = app.CreateAuthenticatedClient();
        var model = await RegisterLocalModelAsync(client, "Orphan");

        var response = await PostLocalResultAsync(client, "01AAAAAAAAAAAAAAAAAAAAAAAA", model);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task LocalResult_FollowsTheSameAnonymousGateAsTheOtherWrites()
    {
        // It used to opt out of authorization entirely, on the premise that the WebLLM worker
        // called it. The client posts it from the app origin with the session cookie attached,
        // so it now honours Features:AllowAnonymousWrites like POST /api/duels and /verdict —
        // which is true in dev/test, hence "not 401" rather than "401" here. The endpoint
        // returns 400 because ModelId is empty; the gate is the salient assertion.
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
