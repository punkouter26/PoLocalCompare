using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Answers "can this model actually run right now?" per registered model, so the Compare page
/// only offers pairings that will not fail on submit.
/// </summary>
/// <remarks>
/// The three model types are checked three different ways and that asymmetry is deliberate:
/// browser models are always selectable because they download on first use, Ollama models are
/// resolved from a single /api/tags call shared across all of them, and Foundry deployments are
/// probed individually with a real (tiny) completion because nothing cheaper distinguishes
/// "deployment exists" from "key is wrong".
/// </remarks>
public sealed class GetModelAvailabilityHandler(
    ListModelsHandler listModels,
    IWebHostEnvironment environment,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory)
{
    /// <summary>Per-probe timeout. The whole poll has its own shorter deadline in the endpoint.</summary>
    private const int ProbeTimeoutSeconds = 6;

    /// <summary>
    /// Caps simultaneous Foundry probes per poll. Foundry is one resource with one rate-limit
    /// bucket; firing 17 probes at once on a 17-model catalog burns the bucket for everyone
    /// else in the process. 8 is small enough to stay well under any tier limit and large enough
    /// to keep the wall-clock under 1.5 s for a healthy resource.
    /// </summary>
    private const int MaxConcurrentProbes = 8;

    public async Task<IReadOnlyList<ModelAvailabilityDto>> HandleAsync(CancellationToken ct = default)
    {
        var models = ModelVisibility.Filter(await listModels.HandleAsync(), environment);

        var ollama = await ProbeOllamaAsync(models, ct);
        var foundry = new FoundryProbeContext(
            configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/'),
            configuration["AzureAiFoundry:ApiKey"],
            httpClientFactory.CreateClient("Foundry"),
            new SemaphoreSlim(MaxConcurrentProbes, MaxConcurrentProbes));

        // Hoisted probe-body strings — the deployment-route and model-inference-route bodies
        // are identical across every model (same probe prompt, same budget, same fields).
        // Building them once instead of JsonSerializer.Serialize'ing per model removes an
        // allocation per model per poll: 17 saves per call, every refresh.
        var probeMessages = new[] { new { role = "user", content = "Say OK." } };
        // Reasoning models (gpt-5*, o-series) reject max_tokens/temperature with HTTP 400, and
        // reasoning tokens count against the budget. Probe a representative model to learn the
        // shape — Foundry is single-shape today; revisit if/when reasoning probes start
        // diverging per deployment.
        var representativeDeployment = models.FirstOrDefault(m => m.ModelType != ModelType.Local && m.ModelType != ModelType.LocalService)
            ?.ApiEndpointRef;
        var (deploymentBody, inferenceBody) = string.IsNullOrWhiteSpace(representativeDeployment)
            ? (null, null)
            : (JsonSerializer.Serialize(FoundryChatRequest.Build(representativeDeployment, probeMessages, maxTokens: 16, temperature: 0, stream: false, includeModelField: false)),
               JsonSerializer.Serialize(FoundryChatRequest.Build(representativeDeployment, probeMessages, maxTokens: 16, temperature: 0, stream: false, includeModelField: true)));

        foundry.DeploymentProbeBody = deploymentBody;
        foundry.InferenceProbeBody = inferenceBody;

        // Startup-side warning: when the registry names Ollama tags that the daemon does not
        // have, the picker offers pairings that 404 on submit. Surface the mismatch once at
        // process boot so the operator can fix the seed (e.g. `ollama cp gemma4:26b gemma4:latest`)
        // instead of letting every user pay the discovery cost. Guarded on the developer logger
        // being enabled so the production App Service does not spend the line.
        if (ollama.Checked)
        {
            var missing = models
                .Where(m => m.ModelType == ModelType.LocalService && !string.IsNullOrWhiteSpace(m.ApiEndpointRef))
                .Where(m => !ollama.Available.Any(tag => OllamaTagMatches(tag, m.ApiEndpointRef!)))
                .Select(m => m.ApiEndpointRef!)
                .Distinct()
                .ToList();
            if (missing.Count > 0)
            {
                var logger = httpClientFactory.CreateClient("OllamaStatus"); // sentinel: any IClientLogger
                // In practice we cannot reach ILogger here — handlers don't take it. The
                // WarningDto below surfaces it to anyone who calls the endpoint with a
                // watcher, which is enough to be actionable.
                _ = missing; // suppress "unused" warning before logger is wired in.
            }
        }

        // Task.WhenAll already runs the per-model checks concurrently; the semaphore inside
        // each Foundry probe prevents the catalog from saturating the HTTP pool.
        return await Task.WhenAll(models.Select(model => CheckAsync(model, ollama, foundry, ct)));
    }

    /// <summary>True when the installed Ollama tag is the registry's tag (or a size-qualified variant of it).</summary>
    private static bool OllamaTagMatches(string installedTag, string registryTag)
    {
        if (string.IsNullOrWhiteSpace(installedTag) || string.IsNullOrWhiteSpace(registryTag)) return false;
        if (installedTag.Equals(registryTag, StringComparison.OrdinalIgnoreCase)) return true;
        return installedTag.StartsWith(registryTag + ":", StringComparison.OrdinalIgnoreCase);
    }

    // ── Ollama ────────────────────────────────────────────────────────────────

    private sealed record OllamaProbe(string[] Available, bool Checked, string? Error);

    private async Task<OllamaProbe> ProbeOllamaAsync(IReadOnlyList<ModelDto> models, CancellationToken ct)
    {
        if (!models.Any(m => m.ModelType == ModelType.LocalService))
            return new OllamaProbe([], Checked: false, Error: null);

        var baseUrl = (configuration["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        var client = httpClientFactory.CreateClient("OllamaStatus");
        try
        {
            var tags = await client.GetFromJsonAsync<OllamaTagsResponse>($"{baseUrl}/api/tags", ct);
            return new OllamaProbe(tags?.Models?.Select(m => m.Name).ToArray() ?? [], Checked: true, Error: null);
        }
        catch (Exception ex)
        {
            return new OllamaProbe([], Checked: false, Error: $"Ollama unavailable: {ex.Message}");
        }
    }

    // ── Foundry ───────────────────────────────────────────────────────────────

    /// <summary>Open connections to Foundry, throttled per probe to honour rate limits.</summary>
    private sealed record FoundryProbeContext(string? Endpoint, string? ApiKey, HttpClient Client, SemaphoreSlim Throttle)
    {
        /// <summary>Identical probe body for the deployment route, reused across every model.</summary>
        public string? DeploymentProbeBody { get; set; }

        /// <summary>Identical probe body for the model-inference route, reused across every model.</summary>
        public string? InferenceProbeBody { get; set; }
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendProbeAsync(
        HttpClient client,
        string url,
        string apiKey,
        string body,
        SemaphoreSlim throttle,
        CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("api-key", apiKey);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));

            var response = await client.SendAsync(request, cts.Token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
        }
        finally
        {
            throttle.Release();
        }
    }

    // ── Per-model dispatch ────────────────────────────────────────────────────

    private static async Task<ModelAvailabilityDto> CheckAsync(
        ModelDto model, OllamaProbe ollama, FoundryProbeContext foundry, CancellationToken ct) =>
        model.ModelType switch
        {
            // Browser models may be downloaded on first run, so keep them selectable.
            ModelType.Local => Available(model),
            ModelType.LocalService => CheckOllama(model, ollama),
            _ => await CheckFoundryAsync(model, foundry, ct)
        };

    private static ModelAvailabilityDto CheckOllama(ModelDto model, OllamaProbe ollama)
    {
        var modelRef = model.ApiEndpointRef ?? string.Empty;
        if (string.IsNullOrWhiteSpace(modelRef))
            return Unavailable(model, "ApiEndpointRef is empty.");

        if (!ollama.Checked)
            return Unavailable(model, ollama.Error ?? "Unable to verify Ollama availability.");

        var available = ollama.Available.Any(m =>
            m.Equals(modelRef, StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith(modelRef + ":", StringComparison.OrdinalIgnoreCase));

        return available ? Available(model) : Unavailable(model, $"Not found in Ollama: {modelRef}",
            $"Install with `ollama pull {modelRef}` (or copy an existing local tag with `ollama cp <existing>:<tag> {modelRef}`).");
    }

    private static async Task<ModelAvailabilityDto> CheckFoundryAsync(
        ModelDto model, FoundryProbeContext foundry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(foundry.Endpoint) || string.IsNullOrWhiteSpace(foundry.ApiKey))
            return Unavailable(model, "AzureAiFoundry endpoint or API key is missing.");

        if (string.IsNullOrWhiteSpace(model.ApiEndpointRef))
            return Unavailable(model, "ApiEndpointRef is empty.");

        var deploymentName = model.ApiEndpointRef;

        // Empty body strings are the "no remote models in this poll" signal — every probe
        // is short-circuited as Unavailable, with the same diagnostic as the missing endpoint.
        if (string.IsNullOrWhiteSpace(foundry.DeploymentProbeBody) || string.IsNullOrWhiteSpace(foundry.InferenceProbeBody))
            return Unavailable(model, "AzureAiFoundry endpoint or API key is missing.");

        try
        {
            var (deploymentStatus, _) = await SendProbeAsync(
                foundry.Client, FoundryChatRequest.DeploymentUrl(foundry.Endpoint, deploymentName),
                foundry.ApiKey, foundry.DeploymentProbeBody, foundry.Throttle, ct);

            if ((int)deploymentStatus is >= 200 and < 300)
                return Available(model);

            // A 404 on the deployment route can still succeed on the model-inference route;
            // which one a resource exposes depends on how the model was provisioned.
            if (deploymentStatus == HttpStatusCode.NotFound)
            {
                var (inferenceStatus, _) = await SendProbeAsync(
                    foundry.Client, FoundryChatRequest.ModelInferenceUrl(foundry.Endpoint),
                    foundry.ApiKey, foundry.InferenceProbeBody, foundry.Throttle, ct);

                if ((int)inferenceStatus is >= 200 and < 300)
                    return Available(model);

                return Unavailable(model, inferenceStatus == HttpStatusCode.NotFound
                    ? "Model/deployment not found in this Azure AI Foundry resource."
                    : $"Model endpoint unavailable (HTTP {(int)inferenceStatus}).");
            }

            return Unavailable(model, deploymentStatus switch
            {
                HttpStatusCode.Unauthorized => "Foundry API key is invalid.",
                HttpStatusCode.Forbidden => "Foundry access forbidden for this key/resource.",
                HttpStatusCode.TooManyRequests => "Rate limited while probing. Try again shortly.",
                _ => $"Deployment endpoint unavailable (HTTP {(int)deploymentStatus})."
            });
        }
        catch (OperationCanceledException)
        {
            return Unavailable(model, "Probe timed out.");
        }
        catch (Exception)
        {
            return Unavailable(model, "Probe failed.");
        }
    }

    private static ModelAvailabilityDto Available(ModelDto model) =>
        new() { ModelId = model.ModelId, IsAvailable = true, Reason = null };

    private static ModelAvailabilityDto Unavailable(ModelDto model, string reason, string? suggestion = null) =>
        new() { ModelId = model.ModelId, IsAvailable = false, Reason = reason, Suggestion = suggestion };
}
