// GoF: Entity
using PoLocalCompare.Domain.Enums;

namespace PoLocalCompare.Domain.Entities;

public sealed class Model
{
    public string ModelId { get; init; }
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

    public Model(
        string modelId,
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
        ModelId = string.Empty;
        DisplayName = string.Empty;
    }
}
