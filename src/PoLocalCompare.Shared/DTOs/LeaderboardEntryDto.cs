using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class LeaderboardEntryDto
{
    public int Rank { get; init; }
    public string ModelId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; init; }
    public int DuelCount { get; init; }
    public int WinCount { get; init; }
    public double WinRate { get; init; }
    public double? OutputQualityAvg { get; init; }
    public double? GreenScoreAvg { get; init; }
    public double? TdpWatts { get; init; }
    public string? WebLlmModelId { get; init; }
    public string? ApiEndpointRef { get; init; }
    public decimal? InputTokenPricePerMillion { get; init; }
    public decimal? OutputTokenPricePerMillion { get; init; }
    public double[]? EloSparkline { get; init; }
}