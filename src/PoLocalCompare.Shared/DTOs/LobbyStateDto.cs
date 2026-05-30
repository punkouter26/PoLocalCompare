namespace PoLocalCompare.Shared.DTOs;

/// <summary>Snapshot of the shared lobby broadcast to all connected clients.</summary>
public sealed class LobbyStateDto
{
    public IReadOnlyList<LobbyPlayerDto> Players { get; init; } = [];
    public string? HostUserId { get; init; }
    /// <summary>True when ≥2 players are connected and all have toggled Ready.</summary>
    public bool CanStart { get; init; }
}
