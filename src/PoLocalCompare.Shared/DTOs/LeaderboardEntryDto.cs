namespace PoLocalCompare.Shared.DTOs;

public sealed class LeaderboardEntryDto
{
    public int Rank { get; init; }
    public string ModelId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public double CurrentElo { get; init; }
    public int DuelCount { get; init; }
    public int WinCount { get; init; }
    public double? GreenScoreAvg { get; init; }
    public IReadOnlyList<double> EloSparkline { get; init; } = [];
}
