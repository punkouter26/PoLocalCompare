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
using PoLocalCompare.Application.Duels.RecordVerdict;
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

    app.UseCors();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference("/scalar");
    }

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
