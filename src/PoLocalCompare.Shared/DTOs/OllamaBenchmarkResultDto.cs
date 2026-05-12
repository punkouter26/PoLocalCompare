namespace PoLocalCompare.Shared.DTOs;

public sealed class OllamaBenchmarkResultDto
{
    public int LoadMs { get; init; }
    public int FirstTokenMs { get; init; }
    public int TokensPerSec { get; init; }
    public int TotalTokens { get; init; }
    public string Output { get; init; } = string.Empty;
    public bool IsFailure { get; init; }
    public string? FailureReason { get; init; }
}
