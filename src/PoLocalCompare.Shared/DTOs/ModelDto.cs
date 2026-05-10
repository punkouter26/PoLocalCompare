using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

public sealed class ModelDto
{
    public string ModelId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; init; }
    public int DuelCount { get; init; }
    public int WinCount { get; init; }
    public double GreenScoreAvg { get; init; }
    public double? TdpWatts { get; init; }
    public string? ApiEndpointRef { get; init; }
    public string? WebLlmModelId { get; init; }
    public decimal? InputTokenPricePerMillion { get; init; }
    public decimal? OutputTokenPricePerMillion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
