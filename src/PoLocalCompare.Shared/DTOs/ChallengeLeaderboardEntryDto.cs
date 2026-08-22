using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>
/// One model's record under challenge budgets.
/// </summary>
/// <remarks>
/// Ranked by how reliably a model comes in under budget, not by rating. That is the whole reason
/// this is a separate table rather than a column on the leaderboard: "wrote the better page" and
/// "was the only one under five seconds" are different claims, and averaging them would produce a
/// number that answers neither question. Challenge duels still move ELO — they are real duels —
/// but this view deliberately does not show it.
/// </remarks>
public sealed class ChallengeLeaderboardEntryDto
{
    public int Rank { get; init; }
    public ModelId ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }

    /// <summary>Challenge duels this model has taken part in.</summary>
    public int Attempts { get; init; }

    /// <summary>Attempts that came in at or under the budget.</summary>
    public int Met { get; init; }

    /// <summary>Share of attempts inside the budget, 0–1. The primary ranking key.</summary>
    public double PassRate { get; init; }

    /// <summary>Challenge duels this model won, however the verdict was reached.</summary>
    public int Wins { get; init; }

    /// <summary>
    /// The model's best measurement for this kind — fastest time, cheapest run, fewest tokens.
    /// Null when it has never produced a usable measurement. Only meaningful within one kind,
    /// which is why the board is filtered by kind rather than mixing all three.
    /// </summary>
    public double? Best { get; init; }

    /// <summary>The kind this row was aggregated over, so the client can format Best correctly.</summary>
    public ChallengeKind Kind { get; init; }
}
