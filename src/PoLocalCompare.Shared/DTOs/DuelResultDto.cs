namespace PoLocalCompare.Shared.DTOs;

public sealed class DuelResultDto
{
    public string DuelId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public long WarmUpDurationMs { get; init; }
    public long GenerationDurationMs { get; init; }
    public long TotalDurationMs { get; init; }
    public int TokenCount { get; init; }
    public double TokenVelocity { get; init; }
    public string HtmlOutputRaw { get; init; } = string.Empty;
    public long HtmlOutputSizeBytes { get; init; }
    public double CharacterDensityRatio { get; init; }
    public bool IsFailure { get; init; }
    public string? FailureReason { get; init; }
    public double? EnergyWh { get; init; }
    public double? EnergyCostUsd { get; init; }
    public double? ApiCostUsd { get; init; }
    public double? GreenScore { get; init; }
}