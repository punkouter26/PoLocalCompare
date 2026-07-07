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
    private const int MaxRetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static async Task EnsureTablesExistAsync(IServiceProvider services)
    {
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment())
            return;

        var logger = services.GetRequiredService<ILogger<TableServiceClient>>();
        var tableServiceClient = services.GetRequiredService<TableServiceClient>();

        foreach (var tableName in RequiredTables)
        {
            var created = false;
            for (var attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    await tableServiceClient.CreateTableIfNotExistsAsync(tableName);
                    logger.LogInformation("Ensured Azure Table Storage table exists: {TableName}", tableName);
                    created = true;
                    break;
                }
                catch (Exception ex)
                {
                    if (attempt < MaxRetries)
                    {
                        logger.LogWarning("Attempt {Attempt}/{Max} — Failed to create table {TableName}. Retrying in {Delay}s. ({Error})",
                            attempt, MaxRetries, tableName, RetryDelay.TotalSeconds, ex.Message);
                        await Task.Delay(RetryDelay);
                    }
                    else
                    {
                        logger.LogError(ex, "Failed to create table {TableName} after {Max} attempts — Azurite may not be running", tableName, MaxRetries);
                    }
                }
            }

            if (!created)
                logger.LogWarning("Table {TableName} could not be created. Start Azurite before the API to avoid storage errors.", tableName);
        }
    }
}
