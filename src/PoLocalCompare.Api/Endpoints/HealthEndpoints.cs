using Azure.Data.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            TableServiceClient tableServiceClient,
            IConfiguration configuration,
            HttpContext context) =>
        {
            var checks = new Dictionary<string, object>();
            var overallHealthy = true;

            // Azure Table Storage check
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await tableServiceClient.GetPropertiesAsync();
                sw.Stop();
                checks["azureTableStorage"] = new { status = "Healthy", latencyMs = sw.ElapsedMilliseconds };
            }
            catch (Exception ex)
            {
                checks["azureTableStorage"] = new { status = "Unhealthy", error = ex.Message };
                overallHealthy = false;
            }

            // Azure AI Foundry check (ping configuration only — avoid API costs)
            var foundryEndpoint = configuration["AzureAiFoundry:Endpoint"];
            if (!string.IsNullOrEmpty(foundryEndpoint))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = await httpClient.GetAsync(foundryEndpoint);
                    sw.Stop();
                    checks["azureAiFoundry"] = new { status = "Healthy", latencyMs = sw.ElapsedMilliseconds };
                }
                catch (Exception ex)
                {
                    checks["azureAiFoundry"] = new { status = "Unhealthy", error = ex.Message };
                    overallHealthy = false;
                }
            }
            else
            {
                checks["azureAiFoundry"] = new { status = "NotConfigured" };
            }

            // Key Vault check
            var keyVaultUri = configuration["KeyVault:Uri"];
            if (!string.IsNullOrEmpty(keyVaultUri))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var response = await httpClient.GetAsync(keyVaultUri);
                    sw.Stop();
                    checks["keyVault"] = new { status = "Healthy", latencyMs = sw.ElapsedMilliseconds };
                }
                catch (Exception ex)
                {
                    checks["keyVault"] = new { status = "Unhealthy", error = ex.Message };
                    overallHealthy = false;
                }
            }
            else
            {
                checks["keyVault"] = new { status = "NotConfigured" };
            }

            var result = new { status = overallHealthy ? "Healthy" : "Unhealthy", checks };
            context.Response.StatusCode = overallHealthy ? 200 : 503;
            return Results.Json(result);
        })
        .WithName("Health")
        .WithTags("Health")
        .WithSummary("Health check for all dependencies");

        app.MapGet("/api/diag/smoke", async (
            TableServiceClient tableServiceClient,
            IModelRepository modelRepository,
            IWebHostEnvironment env,
            HttpContext context) =>
        {
            var checks = new Dictionary<string, object>();
            var overallHealthy = true;

            try
            {
                await tableServiceClient.GetPropertiesAsync();
                checks["azureTableStorage"] = new { status = "Healthy" };
            }
            catch (Exception ex)
            {
                checks["azureTableStorage"] = new { status = "Unhealthy", error = ex.Message };
                overallHealthy = false;
            }

            var allModels = (await modelRepository.GetAllAsync()).ToList();
            var localServiceCount = allModels.Count(m => m.ModelType == ModelType.LocalService);

            var result = new
            {
                status = overallHealthy ? "Healthy" : "Unhealthy",
                environment = env.EnvironmentName,
                models = new
                {
                    total = allModels.Count,
                    localService = localServiceCount,
                    cloudMode = !env.IsDevelopment() && localServiceCount == 0
                },
                checks
            };

            context.Response.StatusCode = overallHealthy ? 200 : 503;
            return Results.Json(result);
        })
        .WithName("DiagnosticsSmoke")
        .WithTags("Health")
        .WithSummary("Quick smoke check for runtime dependencies and model source behavior");

        app.MapGet("/api/diag/warnings", (
            IWebHostEnvironment env,
            ILoggerFactory loggerFactory,
            int? limit) =>
        {
            var logger = loggerFactory.CreateLogger("DiagnosticsWarnings");
            var logsDir = Path.Combine(env.ContentRootPath, "logs");
            var take = Math.Clamp(limit ?? 5, 1, 20);

            if (!Directory.Exists(logsDir))
            {
                return Results.Json(new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    entries = Array.Empty<object>()
                });
            }

            try
            {
                var candidates = Directory.GetFiles(logsDir, "polocalcompare-*.log")
                    .Concat(Directory.GetFiles(logsDir, "polocalcompare-errors-*.log"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .Take(3)
                    .ToList();

                var entries = new List<object>();

                foreach (var file in candidates)
                {
                    // Read only a bounded suffix so diagnostics remain fast.
                    var lines = File.ReadAllLines(file.FullName);
                    foreach (var line in lines.Reverse().Take(400))
                    {
                        if (!(line.Contains("[WRN]") || line.Contains("[ERR]") || line.Contains("[FTL]")))
                            continue;

                        var level = line.Contains("[FTL]") ? "Fatal"
                            : line.Contains("[ERR]") ? "Error"
                            : "Warning";

                        var timestamp = line.Length >= 24 ? line[..24] : string.Empty;
                        var messageStart = line.IndexOf(']');
                        var message = messageStart >= 0 && messageStart + 2 < line.Length
                            ? line[(messageStart + 2)..]
                            : line;

                        entries.Add(new
                        {
                            level,
                            timestamp,
                            message,
                            source = file.Name
                        });

                        if (entries.Count >= take)
                            break;
                    }

                    if (entries.Count >= take)
                        break;
                }

                return Results.Json(new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    entries
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to build diagnostics warning snapshot from log files.");
                return Results.Json(new
                {
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    entries = Array.Empty<object>()
                });
            }
        })
        .WithName("DiagnosticsWarnings")
        .WithTags("Health")
        .WithSummary("Returns recent warning and error events from server logs");

        return app;
    }
}
