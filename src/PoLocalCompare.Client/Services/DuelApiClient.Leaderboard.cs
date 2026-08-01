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
}