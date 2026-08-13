using PoLocalCompare.Shared.Ids;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class LeaderboardEntryDto
{
    public int Rank { get; init; }
    public ModelId ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; init; }
    public int DuelCount { get; init; }
    public int WinCount { get; init; }

    /// <summary>Judged draws. Losses are <c>DuelCount - WinCount - DrawCount</c>.</summary>
    public int DrawCount { get; init; }
    public double WinRate { get; init; }
    public double? OutputQualityAvg { get; init; }
    public double? TdpWatts { get; init; }
    public string? WebLlmModelId { get; init; }
    public string? ApiEndpointRef { get; init; }
    public decimal? InputTokenPricePerMillion { get; init; }
    public decimal? OutputTokenPricePerMillion { get; init; }
    /// <summary>
    /// Average API cost (USD) per duel, aggregated across every result row the model has.
    /// Null when the model has no priced duels yet — distinct from zero, which would mean
    /// "ran and was free" (only possible for unpriced local/Ollama models).
    /// </summary>
    public double? AvgApiCostPerDuel { get; init; }
    public double[]? EloSparkline { get; init; }
}