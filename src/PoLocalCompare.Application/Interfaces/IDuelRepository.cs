// SOLID: Dependency Inversion
using PoLocalCompare.Domain.Entities;

namespace PoLocalCompare.Application.Interfaces;

public interface IDuelRepository
{
    Task<Duel?> GetByIdAsync(string duelId);
    Task SaveAsync(Duel duel);
    Task UpdateAsync(Duel duel);
    Task<IEnumerable<Duel>> ListAsync(int limit, string? beforeMonth);
}
