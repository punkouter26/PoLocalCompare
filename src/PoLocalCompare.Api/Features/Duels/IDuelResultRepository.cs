// SOLID: Dependency Inversion

namespace PoLocalCompare.Api.Features.Duels;

public interface IDuelResultRepository
{
    Task SaveAsync(DuelResult result);
    Task<DuelResult?> GetAsync(string duelId, string modelId);
    Task<IEnumerable<DuelResult>> GetByDuelIdAsync(string duelId);
    Task<IEnumerable<DuelResult>> GetByModelIdAsync(string modelId);
}
