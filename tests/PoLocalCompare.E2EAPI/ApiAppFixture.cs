using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.DTOs;
using Testcontainers.Azurite;

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
                    l.AddFilter("PoLocalCompare.Infrastructure.Persistence", LogLevel.Warning);
                    l.AddFilter("Testcontainers", LogLevel.Warning);
                });

                var mockProxy = new Mock<IRemoteInferenceProxy>();
                mockProxy
                    .Setup(p => p.RunInferenceAsync(
                        It.IsAny<Model>(), It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<Func<int, long, HtmlStreamStats?, Task>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new DuelResult("e2e-duel", "e2e-model")
                    {
                        HtmlOutputRaw = "<html><body>E2E mock</body></html>",
                        TokenCount = 21,
                        TotalDurationMs = 250,
                        IsFailure = false,
                    });
                services.AddScoped(_ => mockProxy.Object);
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
