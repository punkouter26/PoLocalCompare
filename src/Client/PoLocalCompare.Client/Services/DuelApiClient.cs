using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

public sealed class DuelApiClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DuelApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<DuelDto?> CommenceDuelAsync(string leftModelId, string rightModelId, string promptText)
    {
        var body = new { leftModelId, rightModelId, promptText };
        var response = await _http.PostAsJsonAsync("/api/duels", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DuelDto>(JsonOptions);
    }

    public async Task<VerdictResponseDto?> RecordVerdictAsync(string duelId, VerdictRequestDto request)
    {
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/verdict", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerdictResponseDto>(JsonOptions);
    }

    public async Task<DuelDto?> GetDuelAsync(string duelId)
    {
        return await _http.GetFromJsonAsync<DuelDto>($"/api/duels/{duelId}", JsonOptions);
    }

    public async Task<IReadOnlyList<ModelDto>?> GetModelsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelDto>>("/api/models", JsonOptions);
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

    public async Task<IReadOnlyList<LeaderboardEntryDto>?> GetLeaderboardAsync(string sortBy = "Elo")
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<LeaderboardEntryDto>>($"/api/leaderboard?sortBy={Uri.EscapeDataString(sortBy)}", JsonOptions);
    }

    public async Task<IReadOnlyList<HeadToHeadDto>?> GetKillListAsync(string modelId)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<HeadToHeadDto>>($"/api/leaderboard/{modelId}/killlist", JsonOptions);
    }

    public async Task PostLocalResultAsync(
        string duelId,
        string modelId,
        string htmlOutputRaw,
        int tokenCount,
        long totalDurationMs,
        long warmUpDurationMs,
        bool isFailure = false,
        string? failureReason = null)
    {
        var body = new
        {
            modelId,
            htmlOutputRaw,
            tokenCount,
            totalDurationMs,
            warmUpDurationMs,
            isFailure,
            failureReason
        };
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/local-result", body);
        response.EnsureSuccessStatusCode();
    }

    // Extended for Archive (T082)
    public async Task<IReadOnlyList<DuelSummaryDto>?> ListDuelsAsync(int limit = 20, string? before = null)
    {
        var url = $"/api/duels?limit={limit}";
        if (!string.IsNullOrEmpty(before))
            url += $"&before={before}";
        return await _http.GetFromJsonAsync<IReadOnlyList<DuelSummaryDto>>(url, JsonOptions);
    }

    public async Task<byte[]?> DownloadReportAsync(string duelId)
    {
        var response = await _http.GetAsync($"/api/duels/{duelId}/report");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task DevResetAsync()
    {
        var response = await _http.PostAsync("/api/dev/reset", null);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ModelDto?> PatchModelAsync(string modelId, string? displayName, string? apiEndpointRef)
    {
        var body = new { displayName, apiEndpointRef };
        var response = await _http.PatchAsJsonAsync($"/api/models/{Uri.EscapeDataString(modelId)}", body, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModelDto>(JsonOptions);
    }

    /// <summary>Returns GPU vs CPU placement for all models currently loaded in Ollama.
    /// Returns an empty list (never null) if Ollama is unreachable.</summary>
    public async Task<IReadOnlyList<OllamaGpuStatusDto>> GetOllamaGpuStatusAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<IReadOnlyList<OllamaGpuStatusDto>>(
                "/api/ollama/gpu-status", JsonOptions)
                ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Triggers a background server-side download of a local WebLLM model from HuggingFace.
    /// Returns false if the model is not found or the request fails.</summary>
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

    /// <summary>Asks GPT-4.1 Nano to auto-judge the duel. Returns null on failure.</summary>
    public async Task<VerdictResponseDto?> AutoJudgeAsync(string duelId)
    {
        try
        {
            var response = await _http.PostAsync($"/api/duels/{duelId}/auto-judge", null);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<VerdictResponseDto>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns all model names pulled in the local Ollama instance. Never null — empty on failure.</summary>
    public async Task<IReadOnlyList<string>> GetOllamaAvailableModelsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<IReadOnlyList<string>>(
                "/api/ollama/available-models", JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Runs a timed benchmark on an Ollama model via the server. Returns null on network failure.</summary>
    public async Task<OllamaBenchmarkResultDto?> BenchmarkOllamaModelAsync(string modelName, string prompt)
    {
        try
        {
            var body = new { modelName, prompt };
            var response = await _http.PostAsJsonAsync("/api/ollama/benchmark", body, JsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<OllamaBenchmarkResultDto>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed class ModelDownloadStatusResponse
    {
        public bool Downloaded { get; set; }
    }
}
