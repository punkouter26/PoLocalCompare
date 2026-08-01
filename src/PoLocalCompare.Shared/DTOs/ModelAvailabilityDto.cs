using PoLocalCompare.Shared.Ids;
namespace PoLocalCompare.Shared.DTOs;

public sealed class ModelAvailabilityDto
{
    public ModelId ModelId { get; init; }
    public bool IsAvailable { get; init; }
    public string? Reason { get; init; }
}
