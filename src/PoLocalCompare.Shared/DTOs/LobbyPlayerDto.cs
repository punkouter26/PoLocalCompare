namespace PoLocalCompare.Shared.DTOs;

public sealed class LobbyPlayerDto
{
    public string UserId { get; init; } = string.Empty;
    public bool IsHost { get; init; }
    public bool IsReady { get; init; }
    public DateTimeOffset JoinedAt { get; init; }
}
