// SOLID: Dependency Inversion

namespace PoLocalCompare.Api.Features.Leaderboard;

public interface IEloHistoryRepository
{
    Task SaveAsync(EloRecord record);
    /// <summary>Returns the most recent 20 ELO records for sparkline display.</summary>
    Task<IEnumerable<EloRecord>> GetLast20Async(string modelId);
    /// <summary>Returns all ELO records for a model (used by Kill List aggregation).</summary>
    Task<IEnumerable<EloRecord>> GetAllByModelAsync(string modelId);
}
