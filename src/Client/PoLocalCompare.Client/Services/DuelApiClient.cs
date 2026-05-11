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
}
