using System.Net.Http.Json;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

public sealed partial class DuelApiClient
{
    public async Task<IReadOnlyList<ModelDto>?> GetModelsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelDto>>("/api/models", JsonOptions);
    }

    public async Task<IReadOnlyList<ModelAvailabilityDto>?> GetModelAvailabilityAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelAvailabilityDto>>("/api/models/availability", JsonOptions);
    }

    public async Task<ModelDto?> PatchModelAsync(string modelId, string? displayName, string? apiEndpointRef)
    {
        var body = new { displayName, apiEndpointRef };
        var response = await _http.PatchAsJsonAsync($"/api/models/{Uri.EscapeDataString(modelId)}", body, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModelDto>(JsonOptions);
    }

    public async Task<bool> IsLocalModelDownloadedAsync(string webLlmModelId)
    {
        if (string.IsNullOrWhiteSpace(webLlmModelId))
            return false;

        try
        {
            var response = await _http.GetFromJsonAsync<ModelDownloadStatusResponse>(
                $"/api/models/download-status/{Uri.EscapeDataString(webLlmModelId)}", JsonOptions);
            return response?.Downloaded == true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RequestModelDownloadAsync(string webLlmModelId)
    {
        try
        {
            var response = await _http.PostAsync(
                $"/api/models/{Uri.EscapeDataString(webLlmModelId)}/download", null);
            return response.StatusCode == System.Net.HttpStatusCode.Accepted;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ModelDownloadStatusResponse
    {
        public bool Downloaded { get; set; }
    }
}