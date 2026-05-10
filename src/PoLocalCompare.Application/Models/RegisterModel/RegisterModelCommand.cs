// SOLID: Single Responsibility
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Application.Models.RegisterModel;

public sealed record RegisterModelCommand(
    string DisplayName,
    ModelType ModelType,
    double? TdpWatts,
    string? WebLlmModelId,
    string? ApiEndpointRef,
    decimal? InputTokenPricePerMillion,
    decimal? OutputTokenPricePerMillion);
