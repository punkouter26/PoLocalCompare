// SOLID: Dependency Inversion
using PoLocalCompare.Domain.Entities;

namespace PoLocalCompare.Application.Interfaces;

public interface IDuelResultRepository
{
    Task SaveAsync(DuelResult result);
    Task<DuelResult?> GetAsync(string duelId, string modelId);
    Task<IEnumerable<DuelResult>> GetByDuelIdAsync(string duelId);
    Task<IEnumerable<DuelResult>> GetByModelIdAsync(string modelId);
}
