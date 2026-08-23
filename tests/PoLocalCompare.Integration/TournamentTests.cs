using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PoLocalCompare.Integration;

/// <summary>
/// Tournament draw and validation against real storage.
/// </summary>
/// <remarks>
/// Everything asserted here is settled by the time <c>POST /api/tournaments</c> responds — the
/// bracket is drawn and persisted before the runner is queued. That is deliberate: the runner
/// then plays matches in the background for as long as the host lives, so any assertion about
/// how far it has got would race it. What the runner does is covered by unit tests over
/// <c>BracketPlanner</c> and <c>Tournament.RecordWinner</c>, which need no storage at all.
/// </remarks>
[Collection("Integration")]
public sealed class TournamentTests(AzuriteFixture azurite) : IAsyncLifetime
{
    private IntegrationHost _host = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _host = new IntegrationHost(azurite.ConnectionString);
        _client = _host.Client;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private const string Prompt = "Build a self-contained single HTML file with a click counter.";

    private async Task<string> RegisterModelAsync(string name, string modelType = "Remote")
    {
        // A Local model must carry TdpWatts — the registry rejects one without it (the green-stats
        // calculator has nothing to work from otherwise), and WebLlmModelId must be unique, so a
        // shared literal 400s the moment a test registers two browser models.
        object body = modelType == "Local"
            ? new
            {
                DisplayName = name,
                ModelType = modelType,
                WebLlmModelId = $"test-webllm-{name.Replace(" ", "-").ToLowerInvariant()}",
                TdpWatts = 115.0,
            }
            : new { DisplayName = name, ModelType = modelType, ApiEndpointRef = "https://test.endpoint/v1" };

        var response = await _client.PostAsJsonAsync("/api/models", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("modelId").GetString()!;
    }

    private async Task<string[]> RegisterFieldAsync(string prefix, int count) =>
        await Task.WhenAll(Enumerable.Range(1, count).Select(i => RegisterModelAsync($"{prefix} {i}")));

    private async Task<HttpResponseMessage> DrawAsync(IEnumerable<string> modelIds, string prompt = Prompt) =>
        await _client.PostAsJsonAsync("/api/tournaments", new { ModelIds = modelIds, PromptText = prompt });

    private static JsonElement[] MatchesOf(JsonElement tournament) =>
        tournament.GetProperty("matches").EnumerateArray().ToArray();

    // ── Auth ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Entrants_Anonymous_Returns401()
    {
        using var client = _host.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/tournaments/entrants")).StatusCode);
    }

    // ── Entrants ──────────────────────────────────────────────────────────

    /// <summary>
    /// The field is offered strongest-first because that ordering IS the seeding — the form
    /// shows the draw before the user commits to it.
    /// </summary>
    [Fact]
    public async Task Entrants_AreListedStrongestFirst()
    {
        await RegisterFieldAsync("TE Seed", 3);

        var entrants = (await (await _client.GetAsync("/api/tournaments/entrants"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;

        var elos = entrants.Select(e => e.GetProperty("currentElo").GetDouble()).ToArray();
        Assert.Equal(elos.OrderByDescending(e => e), elos);
    }

    /// <summary>
    /// A browser model runs WebGPU inference in a foreground tab, and a bracket is designed to
    /// keep running after that tab closes. Listing one would offer a field that cannot finish.
    /// </summary>
    [Fact]
    public async Task Entrants_ExcludeBrowserModels()
    {
        var browser = await RegisterModelAsync("TE Browser", modelType: "Local");

        var entrants = (await (await _client.GetAsync("/api/tournaments/entrants"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;

        Assert.DoesNotContain(entrants, e => e.GetProperty("modelId").GetString() == browser);
    }
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Draw_AcceptsEverySupportedSize(int count)
    {
        var field = await RegisterFieldAsync($"TE Ok{count}", count);

        var response = await DrawAsync(field);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Draw_RejectsAModelEnteredTwice()
    {
        var field = await RegisterFieldAsync("TE Dupe", 2);

        var response = await DrawAsync([field[0], field[0]]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Draw_RejectsABrowserModelInTheField()
    {
        var remote = await RegisterModelAsync("TE Mixed Remote");
        var browser = await RegisterModelAsync("TE Mixed Browser", modelType: "Local");

        var response = await DrawAsync([remote, browser]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Draw_RejectsAPromptThatIsTooShort()
    {
        var field = await RegisterFieldAsync("TE Short", 2);

        Assert.Equal(HttpStatusCode.BadRequest, (await DrawAsync(field, "hi")).StatusCode);
    }

    [Fact]
    public async Task Draw_RejectsAModelThatIsNotInTheCatalog()
    {
        var real = await RegisterModelAsync("TE Ghost Real");

        var response = await DrawAsync([real, "01NOSUCHMODELIDXXXXXXXXXXX"]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    // ── Persistence ───────────────────────────────────────────────────────

    [Fact]
    public async Task Draw_IsReadableBackByItsId()
    {
        var field = await RegisterFieldAsync("TE Persist", 4);

        var drawn = await (await DrawAsync(field)).Content.ReadFromJsonAsync<JsonElement>();
        var id = drawn.GetProperty("tournamentId").GetString()!;

        var fetched = await (await _client.GetAsync($"/api/tournaments/{id}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, fetched.GetProperty("tournamentId").GetString());
        Assert.Equal(4, fetched.GetProperty("size").GetInt32());
        Assert.Equal(3, MatchesOf(fetched).Length);
        Assert.Equal(Prompt, fetched.GetProperty("promptText").GetString());
    }

    [Fact]
    public async Task Get_UnknownTournament_Returns404()
    {
        var response = await _client.GetAsync("/api/tournaments/01NOSUCHTOURNAMENTIDXXXXXX");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_IncludesADrawnTournament()
    {
        var field = await RegisterFieldAsync("TE List", 2);

        var drawn = await (await DrawAsync(field)).Content.ReadFromJsonAsync<JsonElement>();
        var id = drawn.GetProperty("tournamentId").GetString()!;

        var listed = (await (await _client.GetAsync("/api/tournaments?limit=50"))
            .Content.ReadFromJsonAsync<JsonElement[]>())!;

        Assert.Contains(listed, t => t.GetProperty("tournamentId").GetString() == id);
    }
}
