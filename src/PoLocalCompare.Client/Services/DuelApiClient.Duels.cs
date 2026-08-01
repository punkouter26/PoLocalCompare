using System.Net.Http.Json;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

public sealed partial class DuelApiClient
{
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