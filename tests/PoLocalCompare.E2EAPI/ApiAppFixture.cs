using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PoLocalCompare.Api.Common.Inference;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Judging;
using PoLocalCompare.Api.Features.Leaderboard;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.DTOs;
using Testcontainers.Azurite;

using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.E2EAPI;

/// <summary>
/// Boots the real API (Blazor host) over an ephemeral Azurite container with AI inference mocked,
/// so E2E-API tests exercise the HTTP surface end-to-end as a black box.
/// </summary>
public sealed class ApiAppFixture : IAsyncLifetime
{
    private readonly AzuriteContainer _azurite = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
        .Build();

    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        await _azurite.StartAsync();
        var connectionString = _azurite.GetConnectionString();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); // enables the fake-auth bypass
            builder.UseSetting("ConnectionStrings:AzureTableStorage", connectionString);
            builder.UseSetting("ConnectionStrings:AzureBlobStorage", connectionString);
            builder.UseSetting("Features:UseRealAi", "false");
            builder.UseSetting("KeyVault:Uri", "");
            builder.UseSetting("Testing:SkipSeeding", "true");

            builder.ConfigureServices(services =>
            {
                services.AddLogging(l =>
                {
                    l.AddFilter("PoLocalCompare.Api.Common.Persistence", LogLevel.Warning);
                    l.AddFilter("Testcontainers", LogLevel.Warning);
                });

                // Keyed to match DuelExecutionService's GetRequiredKeyedService lookup, and
                // built per call from the real duel/model so the stored results belong to the
                // duel that produced them. As a plain AddScoped this mock was never resolved,
                // so every duel here ran the real Foundry proxy and failed.
                var mockProxy = new Mock<IRemoteInferenceProxy>();
                mockProxy
                    .Setup(p => p.RunInferenceAsync(
                        It.IsAny<Model>(), It.IsAny<DuelId>(), It.IsAny<string>(),
                        It.IsAny<Func<int, long, HtmlStreamStats?, Task>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((Model model, DuelId duelId, string _, Func<int, long, HtmlStreamStats?, Task> _, CancellationToken _) =>
                        new DuelResult(duelId, model.ModelId)
                        {
                            HtmlOutputRaw = "<html><body>E2E mock</body></html>",
                            TokenCount = 21,
                            TotalDurationMs = 250,
                            IsFailure = false,
                        });
                services.AddKeyedScoped<IRemoteInferenceProxy>("Remote", (_, _) => mockProxy.Object);
                services.AddKeyedScoped<IRemoteInferenceProxy>("LocalService", (_, _) => mockProxy.Object);
            });
        });
    }

    /// <summary>Anonymous client — no auth header.</summary>
    public HttpClient CreateAnonymousClient()
        => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>Client authenticated via the dev/test fake-auth scheme.</summary>
    public HttpClient CreateAuthenticatedClient(string user = "e2e-api", string roles = "User")
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", user);
        client.DefaultRequestHeaders.Add("X-Fake-Roles", roles);
        return client;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _azurite.DisposeAsync();
    }
}

[CollectionDefinition("E2EAPI")]
public sealed class E2EApiCollection : ICollectionFixture<ApiAppFixture> { }
