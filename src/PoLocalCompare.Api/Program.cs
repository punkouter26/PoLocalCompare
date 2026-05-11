using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PoLocalCompare.Api.Endpoints;
using PoLocalCompare.Api.Hubs;
using PoLocalCompare.Api.Services;
using PoLocalCompare.Application.Duels.CommenceDuel;
using PoLocalCompare.Application.Duels.GetDuel;
using PoLocalCompare.Application.Duels.ListDuels;
using PoLocalCompare.Application.Duels.RecordVerdict;
using PoLocalCompare.Application.Archive.ExportLabReport;
using PoLocalCompare.Application.Leaderboard.GetKillList;
using PoLocalCompare.Application.Leaderboard.GetLeaderboard;
using PoLocalCompare.Application.Models.ListModels;
using PoLocalCompare.Application.Models.RegisterModel;
using PoLocalCompare.Infrastructure;
using PoLocalCompare.Infrastructure.Persistence;
using Scalar.AspNetCore;
using Serilog;

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
            .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
            .WriteTo.Console()
            .WriteTo.File(
                path: "logs/polocalcompare-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7);

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
        builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), credential);
    }

    // ─── CORS (T040) ─────────────────────────────────────────────────────────
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
            policy.WithOrigins("http://localhost:5000", "https://localhost:5001")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials());
    });

    // ─── SignalR ──────────────────────────────────────────────────────────────
    builder.Services.AddSignalR();

    // ─── OpenAPI (T020) ───────────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    // ─── Health checks (T038) ────────────────────────────────────────────────
    builder.Services.AddHealthChecks();

    // ─── Razor pages (for /diag) ─────────────────────────────────────────────
    builder.Services.AddRazorPages();

    // ─── JSON serialization — string enum values for API consumers ───────────
    builder.Services.ConfigureHttpJsonOptions(opts =>
    {
        opts.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

    // ─── Infrastructure (Phase 2 — T032–T037) ────────────────────────────────
    builder.Services.AddInfrastructure(builder.Configuration);

    // ─── Application use cases (Phase 3 + 4) ────────────────────────────────
    builder.Services.AddScoped<RegisterModelHandler>();
    builder.Services.AddScoped<ListModelsHandler>();
    builder.Services.AddScoped<CommenceDuelHandler>();
    builder.Services.AddSingleton<DuelExecutionService>();
    // Phase 4 (US2)
    builder.Services.AddScoped<GetDuelHandler>();
    builder.Services.AddScoped<GetLeaderboardHandler>();
    builder.Services.AddScoped<GetKillListHandler>();
    builder.Services.AddScoped<RecordVerdictHandler>(sp =>
    {
        var cfg = sp.GetRequiredService<IConfiguration>();
        var kFactor = cfg.GetValue<double>("Elo:KFactor", 32.0);
        return new RecordVerdictHandler(
            sp.GetRequiredService<PoLocalCompare.Application.Interfaces.IDuelRepository>(),
            sp.GetRequiredService<PoLocalCompare.Application.Interfaces.IModelRepository>(),
            sp.GetRequiredService<PoLocalCompare.Application.Interfaces.IEloHistoryRepository>(),
            kFactor);
    });
    // Phase 6 (US4)
    builder.Services.AddScoped<ListDuelsHandler>();
    builder.Services.AddScoped<ExportLabReportHandler>();

    // ─── Build ───────────────────────────────────────────────────────────────
    var app = builder.Build();

    // ─── Dev-only: ensure Azurite tables exist (T036) ────────────────────────
    if (app.Environment.IsDevelopment())
    {
        await AzuriteSetup.EnsureTablesExistAsync(app.Services);
    }

    // ─── Middleware pipeline ─────────────────────────────────────────────────
    app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diagCtx, httpCtx) =>
        {
            diagCtx.Set("CorrelationId", httpCtx.TraceIdentifier);
            diagCtx.Set("UserId", "anonymous");
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

    app.UseCors();

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

    app.UseStaticFiles();

    app.UseRouting();

    app.MapRazorPages();

    // ─── Health endpoint (T038) ──────────────────────────────────────────────
    app.MapHealthChecks("/health");
    app.MapHealthEndpoints();

    // ─── SignalR hub ─────────────────────────────────────────────────────────
    app.MapHub<DuelHub>("/hubs/duel");

    // ─── API endpoints ────────────────────────────────────────────────────────
    app.MapModelsEndpoints();
    app.MapDuelsEndpoints();
    app.MapLeaderboardEndpoints();

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

// Expose Program class for WebApplicationFactory in integration tests
public partial class Program { }
