using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Shared.DTOs;

/// <summary>SignalR message shape for processing-phase status updates.</summary>
public sealed class ModelStatusUpdateDto
{
    public string DuelId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty; // "Left" | "Right"
    public DuelStatus Status { get; init; }
    public long ElapsedMs { get; init; }
    public int TokenCount { get; init; }
    public double? TokenVelocity { get; init; }
}
