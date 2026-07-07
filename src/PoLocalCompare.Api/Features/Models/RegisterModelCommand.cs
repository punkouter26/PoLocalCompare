// SOLID: Single Responsibility
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

public sealed record RegisterModelCommand(
    string DisplayName,
    ModelType ModelType,
    double? TdpWatts,
    string? WebLlmModelId,
    string? ApiEndpointRef,
    decimal? InputTokenPricePerMillion,
    decimal? OutputTokenPricePerMillion);
