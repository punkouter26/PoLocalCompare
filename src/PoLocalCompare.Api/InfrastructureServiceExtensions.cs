using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using Polly;

namespace PoLocalCompare.Api;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Azure Storage — prefer managed identity in production (AzureStorage__AccountName is set
        // by Bicep and the App Service identity has Storage Table/Blob Data Contributor RBAC roles).
        // Fall back to a connection string for local dev (Azurite via UseDevelopmentStorage=true).
        var storageAccountName = configuration["AzureStorage:AccountName"];
        if (!string.IsNullOrWhiteSpace(storageAccountName))
        {
            var credential = new DefaultAzureCredential();
            services.AddSingleton(new TableServiceClient(
                new Uri($"https://{storageAccountName}.table.core.windows.net"),
                credential));
            services.AddSingleton(new BlobServiceClient(
                new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                credential));
        }
        else
        {
            var tableConnectionString = configuration.GetConnectionString("AzureTableStorage")
                ?? "UseDevelopmentStorage=true";
            var blobConnectionString = configuration.GetConnectionString("AzureBlobStorage")
                ?? "UseDevelopmentStorage=true";
            services.AddSingleton(new TableServiceClient(tableConnectionString));
            services.AddSingleton(new BlobServiceClient(blobConnectionString));
        }

        // Repositories
        services.AddScoped<IModelRepository, ModelRepository>();
        services.AddScoped<IDuelRepository, DuelRepository>();
        services.AddScoped<IEloHistoryRepository, EloHistoryRepository>();
        services.AddScoped<IDuelResultRepository, DuelResultRepository>();
        services.AddScoped<ITournamentRepository, TournamentRepository>();
        services.AddScoped<IChallengeRecordRepository, ChallengeRecordRepository>();

        // Typed HttpClients (standards §5.4) with uniform resilience (§5.6). Retries cover
        // connection-level failures and 5xx/408 before the SSE stream starts; 429 handling is
        // provider-specific because Foundry communicates a Retry-After window that must be honoured.
        // deliberately no per-attempt timeout because it would abort long streaming responses.
        //
        // Foundry timeout: cold-start first-token latency on some smaller deployments (e.g. Phi-4 Mini)
        // exceeds the previous 35 s ceiling, aborting the request before any token can stream. The
        // override key `AzureAiFoundry:RemoteTimeoutSeconds` lets ops tune per environment without a
        // code change. Streaming responses are still guarded by the duel watchdog via CancellationToken.
        var foundryTimeoutSeconds = configuration.GetValue<int?>("AzureAiFoundry:RemoteTimeoutSeconds") ?? 120;
        services.AddHttpClient<FoundryInferenceProxy>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(foundryTimeoutSeconds, 30, 900));
        }).AddResilienceHandler("foundry-inference", AddStreamingRetry);

        services.AddHttpClient<OllamaInferenceProxy>(client =>
        {
            // Timeout is controlled by the CancellationToken watchdog; HttpClient.Timeout must not fire first.
            client.Timeout = Timeout.InfiniteTimeSpan;
        }).AddResilienceHandler("ollama-inference", AddStreamingRetry);

        // Named clients remain for callers outside the typed proxies (Foundry availability probe, Ollama pulls).
        services.AddHttpClient("Foundry", client => client.Timeout = TimeSpan.FromSeconds(35))
            .AddResilienceHandler("foundry-named", AddStreamingRetry);
        services.AddHttpClient("Ollama", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("ollama-ops", AddStreamingRetry);

        // Auto-judge. Its own typed client rather than the streaming one: this call is a single
        // non-streaming completion, so a per-request timeout is safe here (it would abort SSE
        // on the inference clients) and the judge's own linked CTS bounds it further.
        services.Configure<AutoJudgeOptions>(configuration.GetSection(AutoJudgeOptions.SectionName));
        services.AddHttpClient<IDuelJudge, FoundryDuelJudge>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        }).AddResilienceHandler("foundry-judge", AddStreamingRetry);

        // Remote inference proxies — keyed by ModelType name so DuelExecutionService can resolve the right one
        services.AddKeyedTransient<IRemoteInferenceProxy>("Remote", (sp, _) => sp.GetRequiredService<FoundryInferenceProxy>());
        services.AddKeyedTransient<IRemoteInferenceProxy>("LocalService", (sp, _) => sp.GetRequiredService<OllamaInferenceProxy>());

        // Lab report renderer

        // Key Vault
        services.AddKeyVaultSecrets(configuration);

        return services;
    }

    private static void AddStreamingRetry(ResiliencePipelineBuilder<HttpResponseMessage> pipeline) =>
        pipeline.AddRetry(new Polly.Retry.RetryStrategyOptions<HttpResponseMessage>
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(250),
            BackoffType = Polly.DelayBackoffType.Exponential,
            ShouldHandle = new Polly.PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .HandleResult(r => (int)r.StatusCode is >= 500 or 408),
        });
}