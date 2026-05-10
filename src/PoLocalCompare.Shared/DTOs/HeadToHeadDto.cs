namespace PoLocalCompare.Shared.DTOs;

public sealed class HeadToHeadDto
{
    public string OpponentModelId { get; init; } = string.Empty;
    public string OpponentName { get; init; } = string.Empty;
    public int Wins { get; init; }
    public int Losses { get; init; }
    public string LastDuelId { get; init; } = string.Empty;
    public DateTimeOffset LastDuelAt { get; init; }
}
