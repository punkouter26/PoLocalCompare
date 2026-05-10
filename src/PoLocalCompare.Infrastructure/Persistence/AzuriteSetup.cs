using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PoLocalCompare.Infrastructure.Persistence;

/// <summary>
/// Creates Azure Table Storage tables required by the application if they don't already exist.
/// Only called in Development environment (Azurite).
/// </summary>
public static class AzuriteSetup
{
    private static readonly string[] RequiredTables = ["Models", "Duels", "DuelResults", "EloHistory"];

    public static async Task EnsureTablesExistAsync(IServiceProvider services)
    {
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment())
            return;

        var logger = services.GetRequiredService<ILogger<TableServiceClient>>();
        var tableServiceClient = services.GetRequiredService<TableServiceClient>();

        foreach (var tableName in RequiredTables)
        {
            try
            {
                await tableServiceClient.CreateTableIfNotExistsAsync(tableName);
                logger.LogInformation("Ensured Azure Table Storage table exists: {TableName}", tableName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create table {TableName} — Azurite may not be running", tableName);
            }
        }
    }
}
