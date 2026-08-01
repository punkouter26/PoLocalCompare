using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using PoLocalCompare.Api.Common.Inference;
using PoLocalCompare.Api.Features.Duels;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Integration;

/// <summary>
/// One place that knows how to stand the API up against the shared Azurite container.
/// Every suite needs the same five settings and the same inference mock; duplicating that
/// per class is how the copies drift apart.
/// </summary>
/// <remarks>
/// The host runs as <c>Development</c> deliberately — <c>FakeAuthHandler</c> throws if it is
/// constructed in Production, and these tests authenticate with <c>X-Fake-User</c>.
/// </remarks>
public sealed class IntegrationHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public HttpClient Client { get; }

    /// <summary>Services from the running host, for tests that drive repositories directly.</summary>
    public IServiceProvider Services => _factory.Services;

    public IntegrationHost(string connectionString, string user = "integration-test", string? roles = null)
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:AzureTableStorage", connectionString);
                builder.UseSetting("ConnectionStrings:AzureBlobStorage", connectionString);
                builder.UseSetting("Features:UseRealAi", "false");
                builder.UseSetting("KeyVault:Uri", "");            // no Key Vault in tests
                builder.UseSetting("Testing:SkipSeeding", "true");  // suppress ModelSeeder noise
                // The auto-judge would otherwise fire mid-test and race the assertions.
                builder.UseSetting("AiJudge:Enabled", "false");

                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging =>
                    {
                        logging.AddFilter("PoLocalCompare.Api.Common.Persistence", LogLevel.Warning);
                        logging.AddFilter("Testcontainers", LogLevel.Warning);
                    });

                    // Inference is mocked: these tests are about persistence and the HTTP
                    // surface, and a real model call would be slow, costly and non-deterministic.
                    var proxy = new Mock<IRemoteInferenceProxy>();
                    proxy.Setup(p => p.RunInferenceAsync(
                            It.IsAny<Model>(),
                            It.IsAny<DuelId>(),
                            It.IsAny<string>(),
                            It.IsAny<Func<int, long, HtmlStreamStats?, Task>>(),
                            It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new DuelResult(DuelId.From("mock-duel"), ModelId.From("mock-model"))
                        {
                            HtmlOutputRaw = "<html><body>Mock output</body></html>",
                            TokenCount = 42,
                            TotalDurationMs = 500,
                            IsFailure = false,
                        });

                    services.AddScoped(_ => proxy.Object);
                });
            });

        Client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Client.DefaultRequestHeaders.Add("X-Fake-User", user);
        if (roles is not null)
            Client.DefaultRequestHeaders.Add("X-Fake-Roles", roles);
    }

    /// <summary>A client with no fake-auth header, for asserting the deny-by-default policy.</summary>
    public HttpClient CreateAnonymousClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        // Azurite's lifetime belongs to AzuriteFixture — only the host is torn down here.
        await _factory.DisposeAsync();
    }
}
