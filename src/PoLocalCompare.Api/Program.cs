using Azure.Data.Tables;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Azure;
using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Polly;
using OpenTelemetry.Trace;
using PoLocalCompare.Api;
using PoLocalCompare.Api.Auth;
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

    // ─── Key Vault (T037) — must be FIRST before Serilog/OTel config ────────
    var keyVaultUri = builder.Configuration["KeyVault:Uri"];
    if (!string.IsNullOrEmpty(keyVaultUri))
    {
        var credential = new DefaultAzureCredential();
        builder.Configuration.AddAzureKeyVault(
            new Uri(keyVaultUri),
            credential,
            new PrefixKeyVaultSecretManager("PoLocalCompare"));
    }

    // ─── Serilog (T018) — now has KV-provided connection strings ────────────
    builder.Host.UseSerilog((ctx, services, cfg) =>
    {
        cfg
            .ReadFrom.Configuration(ctx.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", "PoLocalCompare.Api")
            .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
            .WriteTo.Console();

        // File sinks are Development-only: on the Free (F1) App Service they consume the
        // limited app filesystem and don't survive restarts/redeploys. In Production all
        // logs flow to Application Insights via the OTel pipeline below.
        if (ctx.HostingEnvironment.IsDevelopment())
        {
            cfg
                .WriteTo.File(
                    path: "logs/polocalcompare-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7)
                .WriteTo.File(
                    path: "logs/polocalcompare-errors-.log",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    restrictedToMinimumLevel: LogEventLevel.Error);
        }

        // Exceptions bypass trace sampling (standards §6.3): Error+ events (with stack traces)
        // ship to Application Insights at 100% regardless of the trace sampler's verdict.
        var serilogAiCs = ctx.Configuration["ApplicationInsights:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(serilogAiCs))
        {
            cfg.WriteTo.ApplicationInsights(
                serilogAiCs,
                TelemetryConverter.Traces,
                restrictedToMinimumLevel: LogEventLevel.Error);
        }
    });

    // ─── OpenTelemetry (T019) — now has KV-provided connection strings ──────
    var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    var aiCs = builder.Configuration["ApplicationInsights:ConnectionString"];
    // Map cloud_RoleName to the execution assembly (reflection, API project only) so traces never
    // surface as "unknown_service:dotnet". With OTel this is the resource service.name.
    var roleName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name ?? "PoLocalCompare.Api";
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(roleName, serviceVersion: "1.0.0"))
        .WithTracing(t =>
        {
            // Standards §6.3: 100% sampling outside Production; Production caps traces at
            // Telemetry:MaxTracesPerSecond (default 10/s). ParentBased keeps full traces together.
            Sampler rootSampler = builder.Environment.IsProduction()
                ? new RateLimitedSampler(builder.Configuration.GetValue("Telemetry:MaxTracesPerSecond", 10d))
                : new AlwaysOnSampler();
            t.SetSampler(new ParentBasedSampler(rootSampler));
            t.AddAspNetCoreInstrumentation(o =>
             {
                 // Strip high-volume, low-value spans: health/diag pings and static SPA assets.
                 o.Filter = httpContext =>
                 {
                     var path = httpContext.Request.Path.Value ?? string.Empty;
                     return !(path.StartsWith("/health", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("/api/diag", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("/css", StringComparison.OrdinalIgnoreCase)
                         || path.StartsWith("/js", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
                         || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase));
                 };
             })
             .AddHttpClientInstrumentation();
            if (!string.IsNullOrEmpty(otlpEndpoint))
                t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            if (!string.IsNullOrWhiteSpace(aiCs))
                t.AddAzureMonitorTraceExporter(o => o.ConnectionString = aiCs);
        })
        .WithMetrics(m =>
        {
            m.AddAspNetCoreInstrumentation()
             .AddHttpClientInstrumentation();
            if (!string.IsNullOrEmpty(otlpEndpoint))
                m.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint));
            if (!string.IsNullOrWhiteSpace(aiCs))
                m.AddAzureMonitorMetricExporter(o => o.ConnectionString = aiCs);
        });

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

    // No CORS: the Blazor WASM client is hosted from this same origin (single-origin
    // topology — the client assets are served from wwwroot by this API), so cross-origin
    // sharing is unnecessary and intentionally omitted.

    // ─── BFF authentication (cookie session + Microsoft OIDC + dev fake) ────────
    builder.AddBffAuthentication();

    // ─── SignalR ──────────────────────────────────────────────────────────────
    builder.Services.AddSignalR();

    // ─── OpenAPI (T020) ───────────────────────────────────────────────────────
    // Standards mandate OpenAPI 3.1; pinned explicitly rather than relying on the
    // SDK default so an SDK change can't silently downgrade the document version.
    builder.Services.AddOpenApi(options =>
        options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1);

    // ─── Razor pages (for /diag) ─────────────────────────────────────────────
    builder.Services.AddRazorPages();

    // ─── Health checks (standards §3) ────────────────────────────────────────
    // /health composes these; /diag renders the same report as the human-readable view,
    // so the two can never disagree about what is up.
    builder.Services.AddHealthChecks()
        .AddCheck<TableStorageHealthCheck>(
            "azureTableStorage",
            tags: [HealthCheckTags.Dependency])
        .AddTypeActivatedCheck<ConfiguredEndpointHealthCheck>(
            "azureAiFoundry",
            HealthStatus.Unhealthy,
            [HealthCheckTags.Dependency],
            "AzureAiFoundry:Endpoint", "Azure AI Foundry")
        .AddTypeActivatedCheck<ConfiguredEndpointHealthCheck>(
            "keyVault",
            HealthStatus.Unhealthy,
            [HealthCheckTags.Dependency],
            "KeyVault:Uri", "Key Vault");

    // ─── JSON serialization — string enum values for API consumers ───────────
    builder.Services.ConfigureHttpJsonOptions(opts =>
    {
        opts.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

    // ─── Infrastructure (Phase 2 — T032–T037) ────────────────────────────────
    // OllamaStatus issues short, idempotent status pings — safe to wrap in a native
    // .NET resilience pipeline (retry + per-attempt timeout). The streaming inference
    // clients (typed Foundry/Ollama proxies in AddInfrastructure) get retry-only pipelines
    // because a per-attempt timeout would abort long SSE responses.
    builder.Services.AddHttpClient("OllamaStatus")
        .AddResilienceHandler("ollama-status", pipeline =>
        {
            pipeline.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = Polly.DelayBackoffType.Exponential,
                ShouldHandle = new Polly.PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
            });
            pipeline.AddTimeout(TimeSpan.FromSeconds(5));
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
        if (!app.Configuration.GetValue<bool>("Testing:SkipSeeding"))
            await ModelSeeder.SeedAsync(app.Services);
    }
    else
    {
        // Fail-fast startup (standards §5.6): an unreachable storage dependency should stop
        // the process now, not surface as request-time 500s later.
        var tables = app.Services.GetRequiredService<TableServiceClient>();
        using var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await tables.GetTableClient("Models").CreateIfNotExistsAsync(startupCts.Token);
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
            // Client-stamped correlation headers (standards §6.9); server ids are the fallback.
            diagCtx.Set("CorrelationId", httpCtx.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? httpCtx.TraceIdentifier);
            diagCtx.Set("UserId", httpCtx.User.Identity?.Name ?? "anonymous");
            diagCtx.Set("RequestPath", httpCtx.Request.Path.Value ?? string.Empty);
            diagCtx.Set("RequestMethod", httpCtx.Request.Method);
            var sessionId = httpCtx.Request.Headers["X-Session-ID"].FirstOrDefault()
                ?? httpCtx.Request.Cookies["X-Session-Id"]
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
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference("/scalar").AllowAnonymous();
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

    app.UseAuthentication();
    app.UseAuthorization();

    // ─── BFF auth routes (/auth/login/microsoft, /auth/login/fake, /auth/logout, /auth/me) ──
    app.MapAuthEndpoints();

    app.MapRazorPages().AllowAnonymous();

    // ─── Health endpoint (T038) ──────────────────────────────────────────────
    app.MapHealthEndpoints();

    // ─── SignalR hubs (auth required) ──────────────────────────────────────────
    app.MapHub<DuelHub>("/hubs/duel").RequireAuthorization();

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
        }).AllowAnonymous();
    }

    // ─── Blazor WASM static assets + fallback (T014) ─────────────────────────
    app.MapStaticAssets().AllowAnonymous();
    app.MapGet("/favicon.ico", () => Results.Redirect("/favicon.png", permanent: true)).AllowAnonymous();
    app.MapFallbackToFile("index.html").AllowAnonymous();

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
