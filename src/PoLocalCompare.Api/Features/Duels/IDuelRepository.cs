// SOLID: Dependency Inversion
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Duels;

public interface IDuelRepository
{
    Task<Duel?> GetByIdAsync(DuelId duelId);
    Task SaveAsync(Duel duel);
    Task UpdateAsync(Duel duel);

    /// <summary>Newest first by duel id (a ULID, so id order is creation order).</summary>
    /// <param name="beforeMonth">Coarse window: partitions <c>le</c> this yyyyMM. Used by the recovery sweeper.</param>
    /// <param name="before">Keyset cursor: only duels whose id sorts strictly below this one — the Archive's Load More.</param>
    /// <param name="verdicts">When non-empty, only duels with one of these verdicts.</param>
    Task<IEnumerable<Duel>> ListAsync(
        int limit,
        string? beforeMonth,
        DuelId? before = null,
        IReadOnlyCollection<DuelVerdict>? verdicts = null);
}
