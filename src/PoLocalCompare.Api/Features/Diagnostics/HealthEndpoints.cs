using Azure.Data.Tables;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Diagnostics;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            TableServiceClient tableServiceClient,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            HttpContext context) =>
        {
            var checks = new Dictionary<string, object>();
            var overallHealthy = true;

            // Azure Table Storage check — use a data-plane operation so that only
            // Storage Table Data Contributor RBAC is required (GetPropertiesAsync is
            // a management-plane call that requires Storage Account Contributor).
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
                await foreach (var _ in tableServiceClient.QueryAsync(maxPerPage: 1, cancellationToken: cts.Token))
                    break;
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
                    var httpClient = httpClientFactory.CreateClient("OllamaStatus");
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
                    var httpClient = httpClientFactory.CreateClient("OllamaStatus");
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
        .WithSummary("Health check for all dependencies")
        .AllowAnonymous();

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
        .WithSummary("Quick smoke check for runtime dependencies and model source behavior")
        .AllowAnonymous();

        return app;
    }
}
