using System.Net.Http.Json;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

public sealed partial class DuelApiClient
{
    public async Task<IReadOnlyList<LeaderboardEntryDto>?> GetLeaderboardAsync(string sortBy = "Elo")
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<LeaderboardEntryDto>>($"/api/leaderboard?sortBy={Uri.EscapeDataString(sortBy)}", JsonOptions);
    }

    public async Task<IReadOnlyList<HeadToHeadDto>?> GetKillListAsync(ModelId modelId)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<HeadToHeadDto>>($"/api/leaderboard/{modelId}/killlist", JsonOptions);
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
}