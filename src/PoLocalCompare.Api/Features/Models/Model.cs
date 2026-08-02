// GoF: Entity
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

public sealed class Model
{
    public ModelId ModelId { get; init; }
    public string DisplayName { get; set; }
    public ModelType ModelType { get; init; }
    public double CurrentElo { get; set; }
    public int DuelCount { get; set; }
    public int WinCount { get; set; }
    public double GreenScoreAvg { get; set; }

    // Local models only
    public double? TdpWatts { get; init; }
    public string? WebLlmModelId { get; init; }

    // Remote models only
    public string? ApiEndpointRef { get; init; }
    public decimal? InputTokenPricePerMillion { get; init; }
    public decimal? OutputTokenPricePerMillion { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Storage concurrency token; set when loaded from Table Storage (standards §5.5).</summary>
    public string? ETag { get; set; }

    public Model(
        ModelId modelId,
        string displayName,
        ModelType modelType,
        double? tdpWatts = null,
        string? webLlmModelId = null,
        string? apiEndpointRef = null,
        decimal? inputTokenPricePerMillion = null,
        decimal? outputTokenPricePerMillion = null)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 100)
            throw new ArgumentException("DisplayName must be 1–100 characters.", nameof(displayName));

        if (modelType == ModelType.Local)
        {
            if (tdpWatts is null or <= 0)
                throw new ArgumentException("TdpWatts is required and must be > 0 for Local models.", nameof(tdpWatts));
            if (string.IsNullOrWhiteSpace(webLlmModelId))
                throw new ArgumentException("WebLlmModelId is required for Local models.", nameof(webLlmModelId));
        }

        if (modelType == ModelType.Remote)
        {
            if (string.IsNullOrWhiteSpace(apiEndpointRef))
                throw new ArgumentException("ApiEndpointRef is required for Remote models.", nameof(apiEndpointRef));
        }

        if (modelType == ModelType.LocalService)
        {
            if (string.IsNullOrWhiteSpace(apiEndpointRef))
                throw new ArgumentException("ApiEndpointRef (Ollama model name, e.g. 'llama3.2') is required for LocalService models.", nameof(apiEndpointRef));
        }

        ModelId = modelId;
        DisplayName = displayName;
        ModelType = modelType;
        CurrentElo = 1200;
        DuelCount = 0;
        WinCount = 0;
        GreenScoreAvg = 0;
        TdpWatts = tdpWatts;
        WebLlmModelId = webLlmModelId;
        ApiEndpointRef = apiEndpointRef;
        InputTokenPricePerMillion = inputTokenPricePerMillion;
        OutputTokenPricePerMillion = outputTokenPricePerMillion;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // Parameterless constructor for Azure Table Storage deserialization
    public Model()
    {
        DisplayName = string.Empty;
    }

    /// <summary>
    /// Returns a copy of this model with the two pricing fields overwritten. Needed because
    /// <see cref="InputTokenPricePerMillion"/> and <see cref="OutputTokenPricePerMillion"/>
    /// are <c>init</c>-only and C#'s init-only semantics track assignments across the instance,
    /// not the constructor — even on a freshly-cloned object the compiler rejects a write.
    /// <see cref="ETag"/> is preserved so the optimistic-concurrency check on the way out still
    /// passes; mutable state (CurrentElo, DuelCount, WinCount, GreenScoreAvg) is taken from
    /// the existing row, not reset to defaults.
    /// </summary>
    public Model WithPricing(decimal? inputPrice, decimal? outputPrice)
    {
        return new Model(
            modelId: ModelId,
            displayName: DisplayName,
            modelType: ModelType,
            tdpWatts: TdpWatts,
            webLlmModelId: WebLlmModelId,
            apiEndpointRef: ApiEndpointRef,
            inputTokenPricePerMillion: inputPrice,
            outputTokenPricePerMillion: outputPrice)
        {
            CurrentElo = CurrentElo,
            DuelCount = DuelCount,
            WinCount = WinCount,
            GreenScoreAvg = GreenScoreAvg,
            CreatedAt = CreatedAt,
            ETag = ETag
        };
    }
}
