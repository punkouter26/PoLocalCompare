using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.DTOs;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Testcontainers.Azurite;

namespace PoLocalCompare.Integration.Tests;

/// <summary>
/// Integration tests for the Duels API endpoints.
/// Uses a real Azurite container for Table Storage and mocks AI inference.
/// </summary>
public sealed class DuelsEndpointTests : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _azurite.StartAsync();

        var connectionString = _azurite.GetConnectionString();

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:AzureTableStorage", connectionString);
                builder.UseSetting("ConnectionStrings:AzureBlobStorage", connectionString);
                builder.UseSetting("Features:UseRealAi", "false");
                builder.UseSetting("KeyVault:Uri", ""); // Skip Key Vault in tests

                builder.ConfigureServices(services =>
                {
                    // Replace Foundry proxy with a mock that returns immediately
                    var mockProxy = new Mock<IRemoteInferenceProxy>();
                    mockProxy
                        .Setup(p => p.RunInferenceAsync(
                            It.IsAny<Model>(),
                            It.IsAny<string>(),
                            It.IsAny<string>(),
                            It.IsAny<Func<int, long, HtmlStreamStats?, Task>>(),
                            It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new DuelResult("test-duel", "test-model")
                        {
                            HtmlOutputRaw = "<html><body>Mock output</body></html>",
                            TokenCount = 42,
                            TotalDurationMs = 500,
                            IsFailure = false,
                        });

                    services.AddScoped(_ => mockProxy.Object);
                });
            });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _azurite.DisposeAsync();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<string> SeedRemoteModelAsync(string modelId, string displayName)
    {
        var payload = new
        {
            DisplayName = displayName,
            ModelType = "Remote",
            ApiEndpointRef = "https://test.endpoint/v1",
            InputTokenPricePerMillion = 1.0m,
            OutputTokenPricePerMillion = 3.0m,
        };
        var response = await _client.PostAsJsonAsync("/api/models", payload);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("modelId").GetString()!;
    }

    // ── POST /api/duels → 202 Accepted ────────────────────────────────────

    [Fact]
    public async Task PostDuel_ValidRequest_Returns202WithDuelId()
    {
        var leftId = await SeedRemoteModelAsync("left-int-1", "Left Model");
        var rightId = await SeedRemoteModelAsync("right-int-1", "Right Model");

        var payload = new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Build a simple HTML counter app.",
        };

        var response = await _client.PostAsJsonAsync("/api/duels", payload);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = body.GetProperty("duelId").GetString();
        Assert.NotNull(duelId);
        Assert.NotEmpty(duelId);
    }

    // ── POST /api/duels → 400 when PromptText is empty ────────────────────

    [Fact]
    public async Task PostDuel_EmptyPrompt_Returns400()
    {
        var leftId = await SeedRemoteModelAsync("left-int-2", "Left Model 2");
        var rightId = await SeedRemoteModelAsync("right-int-2", "Right Model 2");

        var payload = new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "",
        };

        var response = await _client.PostAsJsonAsync("/api/duels", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── POST /api/duels then POST verdict then GET leaderboard full flow ──

    [Fact]
    public async Task FullFlow_CommenceDuel_RecordVerdict_LeaderboardReflectsEloChange()
    {
        var leftId = await SeedRemoteModelAsync("left-flow-1", "Flow Left");
        var rightId = await SeedRemoteModelAsync("right-flow-1", "Flow Right");

        // Step 1: Commence duel
        var commenceResponse = await _client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Build a countdown timer in HTML.",
        });

        Assert.Equal(HttpStatusCode.Accepted, commenceResponse.StatusCode);

        var duelBody = await commenceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = duelBody.GetProperty("duelId").GetString()!;

        // Step 2: Record verdict (left wins)
        var verdictResponse = await _client.PostAsJsonAsync(
            $"/api/duels/{duelId}/verdict",
            new { Verdict = "Left" });

        Assert.Equal(HttpStatusCode.OK, verdictResponse.StatusCode);

        var verdictBody = await verdictResponse.Content.ReadFromJsonAsync<JsonElement>();
        var eloShiftWinner = verdictBody.GetProperty("eloShiftWinner").GetDouble();
        var eloShiftLoser = verdictBody.GetProperty("eloShiftLoser").GetDouble();

        Assert.True(eloShiftWinner > 0, "Winner should gain ELO.");
        Assert.True(eloShiftLoser < 0, "Loser should lose ELO.");

        // Step 3: Verify leaderboard shows updated ELOs
        var leaderboardResponse = await _client.GetAsync("/api/leaderboard");
        Assert.Equal(HttpStatusCode.OK, leaderboardResponse.StatusCode);

        var leaderboardBody = await leaderboardResponse.Content.ReadFromJsonAsync<JsonElement>();
        var entries = leaderboardBody.EnumerateArray().ToList();

        // Top-ranked model should have ELO > 1200 (starting ELO)
        var topEntry = entries.First();
        var topElo = topEntry.GetProperty("currentElo").GetDouble();
        Assert.True(topElo > 1200, $"Top leaderboard ELO should be > 1200 after a win, got {topElo}.");
    }

    // ── POST /api/duels/{id}/verdict → 409 on duplicate ──────────────────

    [Fact]
    public async Task PostVerdict_DuplicateVerdict_Returns409()
    {
        var leftId = await SeedRemoteModelAsync("left-dup-1", "Dup Left");
        var rightId = await SeedRemoteModelAsync("right-dup-1", "Dup Right");

        var commenceResponse = await _client.PostAsJsonAsync("/api/duels", new
        {
            LeftModelId = leftId,
            RightModelId = rightId,
            PromptText = "Write a hello world app.",
        });
        var duelBody = await commenceResponse.Content.ReadFromJsonAsync<JsonElement>();
        var duelId = duelBody.GetProperty("duelId").GetString()!;

        // First verdict
        var first = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Left" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Duplicate verdict → 409
        var second = await _client.PostAsJsonAsync($"/api/duels/{duelId}/verdict", new { Verdict = "Right" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }
}
