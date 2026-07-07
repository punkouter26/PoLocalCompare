// SOLID: Dependency Inversion

namespace PoLocalCompare.Api.Features.Duels;

public interface IDuelRepository
{
    Task<Duel?> GetByIdAsync(string duelId);
    Task SaveAsync(Duel duel);
    Task UpdateAsync(Duel duel);
    Task<IEnumerable<Duel>> ListAsync(int limit, string? beforeMonth);
}
