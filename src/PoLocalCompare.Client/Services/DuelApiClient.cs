using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// The client's whole view of the API. Every call goes through the same
/// <see cref="JsonOptions"/> so enum casing cannot drift between endpoints.
///
/// Kept as one type: this was five partial files totalling ~230 lines, which cost five
/// using-blocks and five namespace declarations to save nothing. Comment banners mark
/// the slices.
/// </summary>
public sealed class DuelApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DuelApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DuelApiClient(HttpClient http, ILogger<DuelApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ── Duels ────────────────────────────────────────────────────────────────

    /// <param name="autoJudgeDelaySeconds">
    /// Per-duel grace window before the AI judge decides. Null keeps the server's configured
    /// value; demo mode passes 0 so an unattended run never stalls waiting for a human pick.
    /// </param>
    public async Task<DuelDto?> CommenceDuelAsync(
        string leftModelId,
        string rightModelId,
        string promptText,
        int? autoJudgeDelaySeconds = null)
    {
        var body = new { leftModelId, rightModelId, promptText, autoJudgeDelaySeconds };
        var response = await _http.PostAsJsonAsync("/api/duels", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DuelDto>(JsonOptions);
    }

    public async Task<DemoPlanDto?> GetDemoPlanAsync(int rounds = 10)
    {
        return await _http.GetFromJsonAsync<DemoPlanDto>($"/api/duels/demo-plan?rounds={rounds}", JsonOptions);
    }

    public async Task<VerdictResponseDto?> RecordVerdictAsync(DuelId duelId, VerdictRequestDto request)
    {
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/verdict", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerdictResponseDto>(JsonOptions);
    }

    public async Task<DuelDto?> GetDuelAsync(DuelId duelId)
    {
        return await _http.GetFromJsonAsync<DuelDto>($"/api/duels/{duelId}", JsonOptions);
    }

    public async Task PostLocalResultAsync(
        DuelId duelId,
        ModelId modelId,
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

    public async Task<IReadOnlyList<DuelSummaryDto>?> ListDuelsAsync(int limit = 20, string? before = null)
    {
        var url = $"/api/duels?limit={limit}";
        if (!string.IsNullOrEmpty(before))
            url += $"&before={before}";
        return await _http.GetFromJsonAsync<IReadOnlyList<DuelSummaryDto>>(url, JsonOptions);
    }

    // ── Leaderboard ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LeaderboardEntryDto>?> GetLeaderboardAsync(string sortBy = "Elo")
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<LeaderboardEntryDto>>(
            $"/api/leaderboard?sortBy={Uri.EscapeDataString(sortBy)}", JsonOptions);
    }

    public async Task<IReadOnlyList<HeadToHeadDto>?> GetKillListAsync(ModelId modelId)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<HeadToHeadDto>>(
            $"/api/leaderboard/{modelId}/killlist", JsonOptions);
    }

    /// <summary>Returns null when either model is unknown, or when both ids are the same model.</summary>
    public async Task<HeadToHeadDetailDto?> GetHeadToHeadAsync(ModelId modelIdA, ModelId modelIdB)
    {
        var response = await _http.GetAsync(
            $"/api/leaderboard/h2h/{Uri.EscapeDataString(modelIdA.Value)}/{Uri.EscapeDataString(modelIdB.Value)}");

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HeadToHeadDetailDto>(JsonOptions);
    }

    // ── Models ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ModelDto>?> GetModelsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelDto>>("/api/models", JsonOptions);
    }

    public async Task<IReadOnlyList<ModelAvailabilityDto>?> GetModelAvailabilityAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelAvailabilityDto>>("/api/models/availability", JsonOptions);
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

    // ── Ollama & diagnostics ─────────────────────────────────────────────────

    public async Task<IReadOnlyList<OllamaGpuStatusDto>> GetOllamaGpuStatusAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<IReadOnlyList<OllamaGpuStatusDto>>(
                "/api/ollama/gpu-status", JsonOptions)
                ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama GPU status unavailable");
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> GetOllamaAvailableModelsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<IReadOnlyList<string>>(
                "/api/ollama/available-models", JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama available-models unavailable");
            return [];
        }
    }

    public async Task<OllamaBenchmarkResultDto?> BenchmarkOllamaModelAsync(string modelName, string prompt)
    {
        try
        {
            var body = new { modelName, prompt };
            var response = await _http.PostAsJsonAsync("/api/ollama/benchmark", body, JsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<OllamaBenchmarkResultDto>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama benchmark failed for model {Model}", modelName);
            return null;
        }
    }

    public async Task<SmokeSnapshotDto?> GetSmokeSnapshotAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<SmokeSnapshotDto>("/api/diag/smoke", JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Smoke snapshot fetch failed");
            return null;
        }
    }

    public sealed class SmokeSnapshotDto
    {
        public string Status { get; set; } = "Unknown";
        public string Environment { get; set; } = "Unknown";
        public SmokeModelsDto Models { get; set; } = new();
    }

    public sealed class SmokeModelsDto
    {
        public int Total { get; set; }
        public int LocalService { get; set; }
        public bool CloudMode { get; set; }
    }
}
