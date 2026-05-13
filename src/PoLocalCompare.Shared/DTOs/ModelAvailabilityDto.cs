namespace PoLocalCompare.Shared.DTOs;

public sealed class ModelAvailabilityDto
{
    public string ModelId { get; init; } = string.Empty;
    public bool IsAvailable { get; init; }
    public string? Reason { get; init; }
}
