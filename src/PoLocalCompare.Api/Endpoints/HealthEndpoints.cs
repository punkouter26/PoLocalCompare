using Azure.Data.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        return app;
    }
}
