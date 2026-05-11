using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Infrastructure.AzureAiFoundry;
using PoLocalCompare.Infrastructure.KeyVault;
using PoLocalCompare.Infrastructure.Persistence.TableStorage;
using PoLocalCompare.Infrastructure.Reporting;

namespace PoLocalCompare.Infrastructure;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Azure Table Storage
        var tableConnectionString = configuration.GetConnectionString("AzureTableStorage")
            ?? configuration["AzureTableStorage:ConnectionString"]
            ?? "UseDevelopmentStorage=true";

        services.AddSingleton(new TableServiceClient(tableConnectionString));

        // Azure Blob Storage
        var blobConnectionString = configuration.GetConnectionString("AzureBlobStorage")
            ?? configuration["AzureBlobStorage:ConnectionString"]
            ?? "UseDevelopmentStorage=true";

        services.AddSingleton(new BlobServiceClient(blobConnectionString));

        // Repositories
        services.AddScoped<IModelRepository, ModelRepository>();
        services.AddScoped<IDuelRepository, DuelRepository>();
        services.AddScoped<IEloHistoryRepository, EloHistoryRepository>();
        services.AddScoped<IDuelResultRepository, DuelResultRepository>();

        // Remote inference proxy
        services.AddScoped<IRemoteInferenceProxy, FoundryInferenceProxy>();

        // Lab report renderer
        services.AddScoped<ILabReportRenderer, HtmlLabReportRenderer>();

        // Key Vault
        services.AddKeyVaultSecrets(configuration);

        return services;
    }
}
