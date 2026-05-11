namespace PoLocalCompare.Shared.DTOs;

public sealed class HeadToHeadDto
{
    public string ModelIdA { get; init; } = string.Empty;
    public string ModelIdB { get; init; } = string.Empty;
    public string OpponentModelId { get; init; } = string.Empty;
    public string OpponentName { get; init; } = string.Empty;
    public int TotalDuels { get; init; }
    public int Wins { get; init; }
    public int Losses { get; init; }
    public int Draws { get; init; }
    public double AvgEloShiftA { get; init; }
    public double AvgEloShiftB { get; init; }
    public string? LastDuelId { get; init; }
    public DateTimeOffset? LastDuelAt { get; init; }
}