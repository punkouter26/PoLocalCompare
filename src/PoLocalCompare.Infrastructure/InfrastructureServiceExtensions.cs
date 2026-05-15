using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Azure.Identity;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Infrastructure.AzureAiFoundry;
using PoLocalCompare.Infrastructure.KeyVault;
using PoLocalCompare.Infrastructure.Ollama;
using PoLocalCompare.Infrastructure.Persistence.TableStorage;
using PoLocalCompare.Infrastructure.Reporting;

namespace PoLocalCompare.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Azure Storage — prefer managed identity in production (AzureStorage__AccountName is set
        // by Bicep and the App Service identity has Storage Table/Blob Data Contributor RBAC roles).
        // Fall back to a connection string for local dev (Azurite via UseDevelopmentStorage=true).
        var storageAccountName = configuration["AzureStorage:AccountName"];
        if (!string.IsNullOrWhiteSpace(storageAccountName))
        {
            var credential = new DefaultAzureCredential();
            services.AddSingleton(new TableServiceClient(
                new Uri($"https://{storageAccountName}.table.core.windows.net"),
                credential));
            services.AddSingleton(new BlobServiceClient(
                new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                credential));
        }
        else
        {
            var tableConnectionString = configuration.GetConnectionString("AzureTableStorage")
                ?? "UseDevelopmentStorage=true";
            var blobConnectionString = configuration.GetConnectionString("AzureBlobStorage")
                ?? "UseDevelopmentStorage=true";
            services.AddSingleton(new TableServiceClient(tableConnectionString));
            services.AddSingleton(new BlobServiceClient(blobConnectionString));
        }

        // Repositories
        services.AddScoped<IModelRepository, ModelRepository>();
        services.AddScoped<IDuelRepository, DuelRepository>();
        services.AddScoped<IEloHistoryRepository, EloHistoryRepository>();
        services.AddScoped<IDuelResultRepository, DuelResultRepository>();

        // Named HttpClient registrations — managed by IHttpClientFactory for proper socket lifecycle
        services.AddHttpClient("Ollama", client =>
        {
            // Timeout is controlled by the CancellationToken watchdog; HttpClient.Timeout must not fire first.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHttpClient("Foundry");

        // Remote inference proxies — keyed by ModelType name so DuelExecutionService can resolve the right one
        services.AddKeyedScoped<IRemoteInferenceProxy, FoundryInferenceProxy>("Remote");
        services.AddKeyedScoped<IRemoteInferenceProxy, OllamaInferenceProxy>("LocalService");

        // Lab report renderer
        services.AddScoped<ILabReportRenderer, HtmlLabReportRenderer>();

        // Key Vault
        services.AddKeyVaultSecrets(configuration);

        return services;
    }
}