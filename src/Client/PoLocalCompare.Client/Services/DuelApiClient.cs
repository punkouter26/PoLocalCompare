using System.Net.Http.Json;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

public sealed class DuelApiClient
{
    private readonly HttpClient _http;

    public DuelApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<DuelDto?> CommenceDuelAsync(string leftModelId, string rightModelId, string promptText)
    {
        var body = new { leftModelId, rightModelId, promptText };
        var response = await _http.PostAsJsonAsync("/api/duels", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DuelDto>();
    }

    public async Task<VerdictResponseDto?> RecordVerdictAsync(string duelId, VerdictRequestDto request)
    {
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/verdict", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerdictResponseDto>();
    }

    public async Task<DuelDto?> GetDuelAsync(string duelId)
    {
        return await _http.GetFromJsonAsync<DuelDto>($"/api/duels/{duelId}");
    }

    public async Task<IReadOnlyList<ModelDto>?> GetModelsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelDto>>("/api/models");
    }

    public async Task<IReadOnlyList<LeaderboardEntryDto>?> GetLeaderboardAsync(string sortBy = "Elo")
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<LeaderboardEntryDto>>($"/api/leaderboard?sortBy={Uri.EscapeDataString(sortBy)}");
    }

    public async Task<IReadOnlyList<HeadToHeadDto>?> GetKillListAsync(string modelId)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<HeadToHeadDto>>($"/api/leaderboard/{modelId}/killlist");
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
        return await _http.GetFromJsonAsync<IReadOnlyList<DuelSummaryDto>>(url);
    }

    public async Task<byte[]?> DownloadReportAsync(string duelId)
    {
        var response = await _http.GetAsync($"/api/duels/{duelId}/report");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }
}
