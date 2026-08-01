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
    public async Task Commence_EchoesTheSubmittedModelsAndPrompt()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, right) = await CommenceAsync(client);

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");

        Assert.Equal(left, duel.GetProperty("leftModelId").GetString());
        Assert.Equal(right, duel.GetProperty("rightModelId").GetString());
        Assert.Contains("kanban", duel.GetProperty("promptText").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Commence_StartsPending()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");

        Assert.Equal("Pending", duel.GetProperty("verdict").GetString());
    }

    [Fact]
    public async Task Commence_EmptyPrompt_Returns400()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Empty Left");
        var right = await RegisterModelAsync(client, "Empty Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Commence_UnknownModel_Returns404()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "Known Left");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = "01NOTAREALMODELID00000000",
            PromptText = "Build an HTML calculator.",
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
    public async Task Commence_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = "01AAAAAAAAAAAAAAAAAAAAAAAA",
            RightModelId = "01BBBBBBBBBBBBBBBBBBBBBBBB",
            PromptText = "Build an HTML calculator.",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
    public async Task GetDuel_ExposesTheAutoJudgeWindow()
    {
        // The Arena counts down from this value; omitting it would make the human-verdict
        // window invisible to the client.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");

        Assert.True(duel.TryGetProperty("autoJudgeDelaySeconds", out _));
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
    public async Task ListDuels_WithoutLimit_UsesTheDefaultInsteadOfFailing()
    {
        // A missing `limit` used to 500; it is optional and defaults to 20.
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/duels");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ListDuels_ClampsAnOversizedLimit()
    {
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/duels?limit=100000");
        var duels = await response.Content.ReadFromJsonAsync<JsonElement[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(duels!.Length <= 100);
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
    public async Task Verdict_NamesTheWinnerAndLoser()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, right) = await CommenceAsync(client);

        var response = await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Right" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(right, body.GetProperty("winnerModelId").GetString());
        Assert.Equal(left, body.GetProperty("loserModelId").GetString());
    }

    [Fact]
    public async Task Verdict_IsRecordedAsAHumanDecision()
    {
        // Standards invariant: every verdict carries a source, and one submitted over the
        // verdict endpoint is by definition a person's.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });
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
    public async Task Verdict_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/duels/01AAAAAAAAAAAAAAAAAAAAAAAA/verdict", new { Verdict = "Left" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Verdict_LeavesTheDuelResolved()
    {
        using var client = app.CreateAuthenticatedClient();
        var (duelId, _, _) = await CommenceAsync(client);

        await client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });
        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");

        Assert.Equal("Left", duel.GetProperty("verdict").GetString());
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
    public async Task LocalResult_IsAcceptedAndAppearsOnTheDuel()
    {
        // This is the browser-inference path: the server never saw the tokens, the client
        // POSTs the finished output. It has to converge with the server-side path.
        using var client = app.CreateAuthenticatedClient();
        var (duelId, left, _) = await CommenceAsync(client);

        var response = await client.PostAsJsonAsync($"/api/duels/{duelId}/local-result", new
        {
            ModelId = left,
            HtmlOutputRaw = "```html\n<html><body>Local</body></html>\n```",
            TokenCount = 55,
            TotalDurationMs = 900L,
            WarmUpDurationMs = 100L,
            IsFailure = false,
        });
        response.EnsureSuccessStatusCode();

        var duel = await client.GetFromJsonAsync<JsonElement>($"/api/duels/{duelId}");
        var results = duel.GetProperty("results").EnumerateArray().ToList();
        Assert.Contains(results, r => r.GetProperty("modelId").GetString() == left);
    }

    [Fact]
    public async Task LocalResult_IsNormalizedBeforeStorage()
    {
        // The markdown fence must be stripped server-side, so a browser model is scored on
        // the same basis as a server-side one.
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
    public async Task LocalResult_Anonymous_Returns401()
    {
        using var client = app.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/duels/01AAAAAAAAAAAAAAAAAAAAAAAA/local-result", new { ModelId = "m" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
