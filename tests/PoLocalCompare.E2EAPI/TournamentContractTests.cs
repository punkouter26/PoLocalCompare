using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Black-box checks on the tournament and challenge HTTP surfaces — the contract a client can
/// rely on, not the arithmetic behind it. Kept to the most behaviour-covering cases per the
/// audit's test ratio; seeding and adjudication logic are covered by the unit tier.
/// </summary>
[Collection("E2EAPI")]
public sealed class TournamentContractTests(ApiAppFixture app)
{
    private const string Prompt = "Build a self-contained single HTML file with a click counter.";

    // ── Deny-by-default ───────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/tournaments/entrants")]
    [InlineData("/api/tournaments")]
    [InlineData("/api/challenges/leaderboard?kind=MaxSeconds")]
    public async Task NewReadEndpoints_AreClosedToAnonymousCallers(string path)
    {
        // FallbackPolicy is RequireAuthenticatedUser, so a new endpoint is closed unless it
        // opts out. These assert the opt-out was not added by accident.
        using var client = app.CreateAnonymousClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Entrants ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Entrants_Returns200WithAnEntrantShape()
    {
        using var client = app.CreateAuthenticatedClient();
        await RegisterModelAsync(client, "Entrant");

        var response = await client.GetAsync("/api/tournaments/entrants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetArrayLength() > 0);

        var first = body.EnumerateArray().First();
        Assert.True(first.TryGetProperty("modelId", out _));
        Assert.True(first.TryGetProperty("displayName", out _));
        Assert.True(first.TryGetProperty("currentElo", out _));
    }

    // ── Draw ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Draw_Returns201WithTheBracketAlreadyPopulated()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "T Left");
        var right = await RegisterModelAsync(client, "T Right");

        var response = await client.PostAsJsonAsync("/api/tournaments", new
        {
            ModelIds = new[] { left, right },
            PromptText = Prompt,
        });

        // The bracket is drawn and persisted before the runner is queued, so the response is
        // the whole bracket rather than a bare id the client has to go and fetch.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("size").GetInt32());
        Assert.Equal(1, body.GetProperty("matches").GetArrayLength());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("tournamentId").GetString()));
    }

    [Theory]
    [InlineData(1, "Build a click counter.")]   // wrong field size
    [InlineData(3, "Build a click counter.")]   // wrong field size
    [InlineData(2, "hi")]                       // prompt under minimum length
    public async Task Draw_RejectsARequestThatFailsBasicValidation(int fieldSize, string prompt)
    {
        using var client = app.CreateAuthenticatedClient();

        var ids = new List<string>();
        for (var i = 0; i < fieldSize; i++)
            ids.Add(await RegisterModelAsync(client, $"T Bad{fieldSize}"));

        var response = await client.PostAsJsonAsync("/api/tournaments", new
        {
            ModelIds = ids,
            PromptText = prompt,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_UnknownTournament_Returns404()
    {
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/tournaments/01NOSUCHTOURNAMENTIDXXXXXX");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Challenge surface ─────────────────────────────────────────────────

    [Fact]
    public async Task ChallengeBoard_Returns200EvenBeforeAnyChallengeHasRun()
    {
        using var client = app.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/challenges/leaderboard?kind=MaxSeconds");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A budget rides along on the ordinary duel request rather than a separate endpoint —
    /// a challenge is a duel with a rule attached.
    /// </summary>
    [Fact]
    public async Task Commence_WithABudget_EchoesItBackOnTheDuel()
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "C Left");
        var right = await RegisterModelAsync(client, "C Right");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = Prompt,
            ChallengeKind = "MaxSeconds",
            ChallengeThreshold = 5.0,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MaxSeconds", body.GetProperty("challengeKind").GetString());
        Assert.Equal(5.0, body.GetProperty("challengeThreshold").GetDouble());
    }

    /// <summary>
    /// A ceiling of zero is not a challenge, it is a duel no model could win — and a missing
    /// budget is just an ordinary duel. Dropped rather than rejected, so a stale client
    /// cannot fail a request over an option it did not mean.
    /// </summary>
    [Theory]
    [InlineData(0.0)]    // zero budget
    [InlineData(-5.0)]   // negative budget
    public async Task Commence_WithANonPositiveBudget_FallsBackToAnOrdinaryDuel(double threshold)
    {
        using var client = app.CreateAuthenticatedClient();
        var left = await RegisterModelAsync(client, "C Zero L");
        var right = await RegisterModelAsync(client, "C Zero R");

        var response = await client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = left,
            RightModelId = right,
            PromptText = Prompt,
            ChallengeKind = "MaxSeconds",
            ChallengeThreshold = threshold,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("None", body.GetProperty("challengeKind").GetString());
    }

    // ── Model profile surface ─────────────────────────────────────────────

    [Fact]
    public async Task Profile_ReturnsTheExpectedShapeForARealModelAnd404ForAnUnknownId()
    {
        using var client = app.CreateAuthenticatedClient();
        var modelId = await RegisterModelAsync(client, "P Shape");

        var ok = await client.GetAsync($"/api/leaderboard/{modelId}/profile");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(modelId, body.GetProperty("modelId").GetString());
        Assert.True(body.TryGetProperty("eloHistory", out _));
        Assert.True(body.TryGetProperty("killList", out _));
        Assert.True(body.TryGetProperty("winningOutputs", out _));

        // The page contract differs from the kill-list: an unknown id there degrades to "no
        // history" because history is stored per-pair, while a profile is about a model and
        // a model the catalog does not know about is genuinely not found.
        var missing = await client.GetAsync("/api/leaderboard/01NOSUCHMODELIDXXXXXXXXXXX/profile");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task<string> RegisterModelAsync(HttpClient client, string prefix)
    {
        var response = await client.PostAsJsonAsync("/api/models", new
        {
            DisplayName = $"{prefix} {Guid.NewGuid():N}",
            ModelType = "Remote",
            ApiEndpointRef = "demo-deployment",
            InputTokenPricePerMillion = 0.10m,
            OutputTokenPricePerMillion = 0.30m,
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }
}
