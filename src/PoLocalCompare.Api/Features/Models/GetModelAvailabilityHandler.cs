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
    private const int ProbeTimeoutSeconds = 6;

    public async Task<IReadOnlyList<ModelAvailabilityDto>> HandleAsync(CancellationToken ct = default)
    {
        var models = ModelVisibility.Filter(await listModels.HandleAsync(), environment);

        var ollama = await ProbeOllamaAsync(models, ct);
        var foundry = new FoundryProbeContext(
            configuration["AzureAiFoundry:Endpoint"]?.TrimEnd('/'),
            configuration["AzureAiFoundry:ApiKey"],
            httpClientFactory.CreateClient("Foundry"));

        return await Task.WhenAll(models.Select(model => CheckAsync(model, ollama, foundry, ct)));
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

    private sealed record FoundryProbeContext(string? Endpoint, string? ApiKey, HttpClient Client);

    private static async Task<(HttpStatusCode StatusCode, string Body)> SendProbeAsync(
        HttpClient client, string url, string apiKey, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("api-key", apiKey);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(ProbeTimeoutSeconds));

        var response = await client.SendAsync(request, cts.Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cts.Token));
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

        return available ? Available(model) : Unavailable(model, $"Not found in Ollama: {modelRef}");
    }

    private static async Task<ModelAvailabilityDto> CheckFoundryAsync(
        ModelDto model, FoundryProbeContext foundry, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(foundry.Endpoint) || string.IsNullOrWhiteSpace(foundry.ApiKey))
            return Unavailable(model, "AzureAiFoundry endpoint or API key is missing.");

        if (string.IsNullOrWhiteSpace(model.ApiEndpointRef))
            return Unavailable(model, "ApiEndpointRef is empty.");

        var deploymentName = model.ApiEndpointRef;
        var probeMessages = new[] { new { role = "user", content = "Say OK." } };

        // Reasoning models (gpt-5*, o-series) reject max_tokens/temperature with HTTP 400.
        // Reasoning tokens count against the budget, so probe with enough headroom to return a token.
        var deploymentBody = JsonSerializer.Serialize(FoundryChatRequest.Build(
            deploymentName, probeMessages, maxTokens: 16, temperature: 0, stream: false, includeModelField: false));
        var inferenceBody = JsonSerializer.Serialize(FoundryChatRequest.Build(
            deploymentName, probeMessages, maxTokens: 16, temperature: 0, stream: false, includeModelField: true));

        try
        {
            var (deploymentStatus, _) = await SendProbeAsync(
                foundry.Client, FoundryChatRequest.DeploymentUrl(foundry.Endpoint, deploymentName),
                foundry.ApiKey, deploymentBody, ct);

            if ((int)deploymentStatus is >= 200 and < 300)
                return Available(model);

            // A 404 on the deployment route can still succeed on the model-inference route;
            // which one a resource exposes depends on how the model was provisioned.
            if (deploymentStatus == HttpStatusCode.NotFound)
            {
                var (inferenceStatus, _) = await SendProbeAsync(
                    foundry.Client, FoundryChatRequest.ModelInferenceUrl(foundry.Endpoint),
                    foundry.ApiKey, inferenceBody, ct);

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

    private static ModelAvailabilityDto Unavailable(ModelDto model, string reason) =>
        new() { ModelId = model.ModelId, IsAvailable = false, Reason = reason };
}
