// SOLID: Dependency Inversion

namespace PoLocalCompare.Api.Features.Duels;

public interface IDuelResultRepository
{
    Task SaveAsync(DuelResult result);
    Task<DuelResult?> GetAsync(DuelId duelId, ModelId modelId);
    Task<IEnumerable<DuelResult>> GetByDuelIdAsync(DuelId duelId);
    Task<IEnumerable<DuelResult>> GetByModelIdAsync(ModelId modelId);
}
