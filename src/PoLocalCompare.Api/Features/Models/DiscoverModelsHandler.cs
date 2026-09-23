using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Lists models that can be added to the catalog at runtime: browser builds the vendored WebLLM
/// bundle knows how to load, and Ollama tags the local daemon has pulled — minus whatever is
/// already registered.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <see cref="ModelSeeder"/> only seeds an empty table, so adding a model
/// used to mean editing the seeder and wiping Azurite. A model added here is an ordinary row;
/// nothing about duels, ELO or the leaderboard distinguishes it from a seeded one.
/// </para>
/// <para>
/// What it does NOT do is vendor weights. <c>download-models.py</c> reads its list from the
/// seeder, so a browser model added here streams its weights from the Hub CDN on first use
/// unless someone adds it to the seeder and re-runs the script. That is the same bimodal
/// behaviour any browser model has when <c>wwwroot/models/</c> is absent.
/// </para>
/// </remarks>
public sealed class DiscoverModelsHandler(
    IModelRepository modelRepository,
    ListOllamaModelsHandler ollamaModels,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment environment,
    HybridCache cache,
    ILogger<DiscoverModelsHandler> logger)
{
    /// <summary>Named client for the Hub's public model API.</summary>
    public const string HubClient = "HuggingFaceHub";

    /// <summary>The org that publishes every MLC-compiled model the bundle references.</summary>
    private const string MlcOrg = "mlc-ai";

    public async Task<ModelDiscoveryDto> HandleAsync(CancellationToken ct = default)
    {
        var registered = (await modelRepository.GetAllAsync()).ToList();
        var bundle = await ReadBundleAsync(ct);
        var hub = await ReadHubStatsAsync(ct);
        var ollamaSupported = environment.IsDevelopment();

        var takenBrowserIds = registered
            .Where(m => m.WebLlmModelId is not null)
            .Select(m => m.WebLlmModelId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenOllamaTags = registered
            .Where(m => m.ModelType == ModelType.LocalService && m.ApiEndpointRef is not null)
            .Select(m => m.ApiEndpointRef!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var browser = (bundle ?? [])
            .Where(e => !takenBrowserIds.Contains(e.ModelId))
            .Select(e =>
            {
                var stats = e.HubRepo is not null && hub is not null
                    ? hub.GetValueOrDefault(e.HubRepo)
                    : default;
                return new DiscoveredBrowserModelDto
                {
                    WebLlmModelId = e.ModelId,
                    HubRepo = e.HubRepo,
                    SuggestedName = WebLlmBundleCatalog.SuggestName(e.ModelId),
                    VramRequiredMb = e.VramRequiredMb,
                    LowResource = e.LowResource,
                    Downloads = stats.Downloads,
                    Likes = stats.Likes,
                };
            })
            // Most-used first: of 150-odd builds, the popular ones are what people come for.
            .OrderByDescending(d => d.Downloads ?? -1)
            .ThenBy(d => d.WebLlmModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ollama = ollamaSupported
            ? (await ollamaModels.HandleAsync(ct))
                .Where(tag => !takenOllamaTags.Contains(tag))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(tag => new DiscoveredOllamaModelDto
                {
                    Name = tag,
                    SuggestedName = WebLlmBundleCatalog.SuggestOllamaName(tag),
                })
                .ToList()
            : [];

        return new ModelDiscoveryDto
        {
            Browser = browser,
            Ollama = ollama,
            BundleAvailable = bundle is not null,
            HubReachable = hub is not null,
            OllamaSupported = ollamaSupported,
        };
    }

    /// <summary>
    /// The bundle's model list, or null when the file is absent or is an LFS pointer. Cached
    /// for the process lifetime in effect — the file cannot change without a redeploy — but
    /// through HybridCache rather than a static, so a test host gets its own.
    /// </summary>
    public async Task<IReadOnlyList<WebLlmBundleCatalog.Entry>?> ReadBundleAsync(CancellationToken ct = default)
    {
        var entries = await cache.GetOrCreateAsync(
            "webllm-bundle-catalog",
            async token =>
            {
                var file = environment.WebRootFileProvider.GetFileInfo("js/web-llm.js");
                if (!file.Exists || file.Length < WebLlmBundleCatalog.MinimumBundleBytes)
                {
                    logger.LogWarning(
                        "web-llm.js is {State}; browser-model discovery is disabled. A clone made without `git lfs install` has a pointer file here.",
                        file.Exists ? $"only {file.Length} bytes" : "missing");
                    return Array.Empty<WebLlmBundleCatalog.Entry>();
                }

                await using var stream = file.CreateReadStream();
                using var reader = new StreamReader(stream);
                return WebLlmBundleCatalog.Parse(await reader.ReadToEndAsync(token)).ToArray();
            },
            new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(12), LocalCacheExpiration = TimeSpan.FromHours(12) },
            cancellationToken: ct);

        return entries.Length == 0 ? null : entries;
    }

    /// <summary>
    /// Download and like counts for every <c>mlc-ai</c> repo, in one request. Null when the Hub
    /// is unreachable — discovery still works, it just cannot rank by popularity.
    /// </summary>
    private async Task<Dictionary<string, (long? Downloads, long? Likes)>?> ReadHubStatsAsync(CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient(HubClient);
            await using var stream = await http.GetStreamAsync($"/api/models?author={MlcOrg}&limit=1000", ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var stats = new Dictionary<string, (long?, long?)>(StringComparer.OrdinalIgnoreCase);
            foreach (var repo in document.RootElement.EnumerateArray())
            {
                if (!repo.TryGetProperty("id", out var id) || id.GetString() is not { } name) continue;
                stats[name] = (ReadLong(repo, "downloads"), ReadLong(repo, "likes"));
            }
            return stats;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogInformation(ex, "Hugging Face Hub unreachable; listing browser models without popularity.");
            return null;
        }
    }

    private static long? ReadLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : null;
}
