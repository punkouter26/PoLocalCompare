// GoF: Entity
namespace PoLocalCompare.Api.Features.Duels;

public sealed class DuelResult
{
    public string DuelId { get; init; }
    public string ModelId { get; init; }
    public long WarmUpDurationMs { get; set; }
    public long GenerationDurationMs { get; set; }
    public long TotalDurationMs { get; set; }
    public int TokenCount { get; set; }
    public double TokenVelocity { get; set; }
    public string HtmlOutputRaw { get; set; }
    public long HtmlOutputSizeBytes { get; set; }
    public double CharacterDensityRatio { get; set; }
    public int OutputQualityScore { get; set; }
    public bool IsFailure { get; set; }
    public string? FailureReason { get; set; }

    // Local models only
    public double? EnergyWh { get; set; }
    public double? EnergyCostUsd { get; set; }

    // Remote models only
    public double? ApiCostUsd { get; set; }

    // Local models only
    public double? GreenScore { get; set; }

    public DuelResult(string duelId, string modelId)
    {
        DuelId = duelId;
        ModelId = modelId;
        HtmlOutputRaw = string.Empty;
    }

    // Parameterless constructor for Azure Table Storage deserialization
    public DuelResult()
    {
        DuelId = string.Empty;
        ModelId = string.Empty;
        HtmlOutputRaw = string.Empty;
    }
}
