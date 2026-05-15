using Azure.Data.Tables;
using Azure;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PoLocalCompare.Api;
using PoLocalCompare.Api.Endpoints;
using PoLocalCompare.Api.Hubs;
using PoLocalCompare.Api.Infrastructure;
using PoLocalCompare.Api.Services;
using PoLocalCompare.Infrastructure;
using PoLocalCompare.Infrastructure.Persistence;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using System.IO;

// ─── Bootstrap logger (before DI) ───────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ─── Serilog (T018) ─────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
    {
        cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "PoLocalCompare.Api")
            .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
            .WriteTo.Console()
            .WriteTo.File(
                path: "logs/polocalcompare-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .WriteTo.File(
                path: "logs/polocalcompare-errors-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Error);

        // Only attach Application Insights sink when the telemetry configuration is available
        var telemetry = services.GetService<Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration>();
        if (telemetry is not null)
        {
            cfg.WriteTo.ApplicationInsights(telemetry, TelemetryConverter.Traces);
        }
    });

    // ─── OpenTelemetry (T019) ────────────────────────────────────────────────
    var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("PoLocalCompare", serviceVersion: "1.0.0"))
        .WithTracing(t =>
        {
            t.AddAspNetCoreInstrumentation()
             .AddHttpClientInstrumentation();
            if (!string.IsNullOrEmpty(otlpEndpoint))
                t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        })
        .WithMetrics(m =>
        {
            m.AddAspNetCoreInstrumentation()
             .AddHttpClientInstrumentation();
            if (!string.IsNullOrEmpty(otlpEndpoint))
                m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
        });

    // ─── Key Vault (T037) ────────────────────────────────────────────────────
    var keyVaultUri = builder.Configuration["KeyVault:Uri"];
    if (!string.IsNullOrEmpty(keyVaultUri))
    {
        var credential = new DefaultAzureCredential();
        builder.Configuration.AddAzureKeyVault(
            new Uri(keyVaultUri),
            credential,
            new PrefixKeyVaultSecretManager("PoLocalCompare"));
    }

    // In Development, Key Vault holds production storage connection strings.
    // Override them back to Azurite so local runs don't hit the real storage account.
    if (builder.Environment.IsDevelopment())
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["ConnectionStrings:AzureTableStorage"]))
            builder.Configuration["ConnectionStrings:AzureTableStorage"] = "UseDevelopmentStorage=true";

        if (string.IsNullOrWhiteSpace(builder.Configuration["ConnectionStrings:AzureBlobStorage"]))
            builder.Configuration["ConnectionStrings:AzureBlobStorage"] = "UseDevelopmentStorage=true";
    }

    if (builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["AzureAiFoundry:ApiKey"]))
    {
        Log.Warning("AzureAiFoundry:ApiKey is empty. Configure AzureAiFoundry__ApiKey via user-secrets or environment variables for remote model duels.");
    }

    // ─── SignalR ──────────────────────────────────────────────────────────────
    builder.Services.AddSignalR();

    // ─── OpenAPI (T020) ───────────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    // ─── Razor pages (for /diag) ─────────────────────────────────────────────
    builder.Services.AddRazorPages();

    // ─── JSON serialization — string enum values for API consumers ───────────
    builder.Services.ConfigureHttpJsonOptions(opts =>
    {
        opts.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

    // ─── Infrastructure (Phase 2 — T032–T037) ────────────────────────────────
    builder.Services.AddHttpClient("OllamaStatus", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });
    // Foundry client: cap at 35 s to allow for network latency while preventing indefinite hang
    builder.Services.AddHttpClient("Foundry", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(35);
    });
    builder.Services.AddInfrastructure(builder.Configuration);

    // ─── Application use cases (Phase 3 + 4 + 6) ────────────────────────────
    builder.Services.AddApplicationServices();

    // ─── Background task queue for reliable duel execution ──────────────────
    builder.Services.AddSingleton<BackgroundTaskQueue>();
    builder.Services.AddSingleton<IBackgroundTaskQueue>(sp => sp.GetRequiredService<BackgroundTaskQueue>());
    builder.Services.AddHostedService(sp => new BackgroundTaskService(
        sp.GetRequiredService<IBackgroundTaskQueue>(),
        sp.GetRequiredService<ILogger<BackgroundTaskService>>()));

    // ─── Build ───────────────────────────────────────────────────────────────
    var app = builder.Build();

    ProgramBootstrapVerifier.VerifyClientBootstrapAssets(app);

    // ─── Dev-only: ensure Azurite tables exist (T036) ────────────────────────
    if (app.Environment.IsDevelopment())
    {
        await AzuriteSetup.EnsureTablesExistAsync(app.Services);
        await PoLocalCompare.Infrastructure.Persistence.ModelSeeder.SeedAsync(app.Services);
    }

    // ─── Middleware pipeline ─────────────────────────────────────────────────
    app.UseSerilogRequestLogging(opts =>
    {
        opts.GetLevel = (httpCtx, elapsed, ex) =>
        {
            if (ex is not null || httpCtx.Response.StatusCode >= 500)
                return LogEventLevel.Error;

            if (httpCtx.Response.StatusCode >= 400)
                return LogEventLevel.Warning;

            var path = httpCtx.Request.Path;
            if (path.StartsWithSegments("/_framework")
                || path.StartsWithSegments("/css")
                || path.StartsWithSegments("/js")
                || path.Value?.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase) == true)
            {
                return LogEventLevel.Debug;
            }

            return LogEventLevel.Information;
        };

        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            diagCtx.Set("CorrelationId", httpCtx.TraceIdentifier);
            diagCtx.Set("UserId", "anonymous");
            diagCtx.Set("RequestPath", httpCtx.Request.Path.Value ?? string.Empty);
            diagCtx.Set("RequestMethod", httpCtx.Request.Method);
            // SessionId: read from cookie or generate a new one for traceability
            var sessionId = httpCtx.Request.Cookies["X-Session-Id"]
                ?? httpCtx.TraceIdentifier;
            diagCtx.Set("SessionId", sessionId);
        };
    });

    // ─── Global exception handler (T088) — RFC 7807 problem+json ────────────
    app.UseExceptionHandler(exceptionApp =>
    {
        exceptionApp.Run(async ctx =>
        {
            ctx.Response.ContentType = "application/problem+json";
            var feature = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
            var ex = feature?.Error;
            var env = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
            var correlationId = ctx.TraceIdentifier;

            logger.LogError(ex,
                "Unhandled exception. CorrelationId: {CorrelationId}, Environment: {Environment}, UserId: anonymous",
                correlationId, env.EnvironmentName);

            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;

            var problem = new
            {
                type = "https://tools.ietf.org/html/rfc7807",
                title = "An unexpected error occurred.",
                status = StatusCodes.Status500InternalServerError,
                detail = env.IsDevelopment() ? ex?.Message : "An internal server error occurred.",
                correlationId,
                stackTrace = env.IsDevelopment() ? ex?.StackTrace : null,
            };

            await ctx.Response.WriteAsJsonAsync(problem);
        });
    });

    // ─── Content-Security-Policy: frame-ancestors 'self' (T089) ─────────────
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";
        await next(ctx);
    });

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference("/scalar");
    }

    // Force browsers to revalidate WASM framework assets on every load (UX #9)
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/_framework"))
            ctx.Response.Headers.CacheControl = "no-cache, must-revalidate";
        await next();
    });

    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();

    app.UseRouting();

    app.MapRazorPages();

    // ─── Health endpoint (T038) ──────────────────────────────────────────────
    app.MapHealthEndpoints();

    // ─── SignalR hub ─────────────────────────────────────────────────────────
    app.MapHub<DuelHub>("/hubs/duel");

    // ─── API endpoints ────────────────────────────────────────────────────────
    app.MapModelsEndpoints();
    app.MapDuelsEndpoints();
    app.MapLeaderboardEndpoints();
    app.MapOllamaEndpoints();

    // ─── Dev-only: wipe duels/results/elo and reset model stats ─────────────
    if (app.Environment.IsDevelopment())
    {
        static async Task ClearTableEntitiesAsync(TableClient tableClient)
        {
            await foreach (var entity in tableClient.QueryAsync<TableEntity>())
            {
                try
                {
                    await tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                }
                catch (RequestFailedException ex)
                    when (ex.Status == 404)
                {
                    // Entity already removed; continue.
                }
            }
        }

        app.MapPost("/api/dev/reset", async (TableServiceClient tsc) =>
        {
            foreach (var t in new[] { "Duels", "DuelResults", "EloHistory" })
            {
                var table = tsc.GetTableClient(t);
                await table.CreateIfNotExistsAsync();
                await ClearTableEntitiesAsync(table);
            }

            var mc = tsc.GetTableClient("Models");
            await mc.CreateIfNotExistsAsync();
            await foreach (var e in mc.QueryAsync<TableEntity>(x => x.PartitionKey == "model"))
            {
                e["CurrentElo"] = 1200.0;
                e["DuelCount"] = 0;
                e["WinCount"] = 0;
                e["GreenScoreAvg"] = 0.0;
                await mc.UpsertEntityAsync(e, TableUpdateMode.Replace);
            }
            return Results.Ok(new { reset = true, message = "Duels/results/elo cleared; model ELO reset to 1200" });
        });
    }

    // ─── Blazor WASM static assets + fallback (T014) ─────────────────────────
    app.MapStaticAssets();
    app.MapFallbackToFile("index.html");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "PoLocalCompare.Api failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program { }

static class ProgramBootstrapVerifier
{
    public static void VerifyClientBootstrapAssets(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "PoLocalCompare.Client.staticwebassets.endpoints.json"),
            Path.Combine(AppContext.BaseDirectory, "PoLocalCompare.Api.staticwebassets.endpoints.json"),
        };

        string? manifestPath = candidates.FirstOrDefault(File.Exists);
        if (manifestPath is null)
        {
            logger.LogError("Startup verification failed: static web asset manifest was not found in {BaseDirectory}", AppContext.BaseDirectory);
            return;
        }

        string manifestContent;
        try
        {
            manifestContent = File.ReadAllText(manifestPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup verification failed: could not read static web asset manifest at {ManifestPath}", manifestPath);
            return;
        }

        bool hasFingerprintedBootstrap = manifestContent.Contains("_framework/blazor.webassembly.", StringComparison.OrdinalIgnoreCase)
            && manifestContent.Contains(".js", StringComparison.OrdinalIgnoreCase);
        bool hasNonFingerprintedBootstrap = manifestContent.Contains("_framework/blazor.webassembly.js", StringComparison.OrdinalIgnoreCase);

        if (!hasFingerprintedBootstrap)
        {
            logger.LogError("Startup verification failed: no Blazor WebAssembly bootstrap asset mapping was found in {ManifestPath}", manifestPath);
            return;
        }

        if (!hasNonFingerprintedBootstrap)
        {
            logger.LogWarning("Startup verification: manifest has only fingerprinted Blazor bootstrap mappings. Ensure index.html resolves the fingerprinted _framework/blazor.webassembly asset.");
        }
        else
        {
            logger.LogInformation("Startup verification: Blazor bootstrap static asset mappings were found in {ManifestPath}", manifestPath);
        }
    }
}
