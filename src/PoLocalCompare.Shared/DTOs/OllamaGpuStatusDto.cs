namespace PoLocalCompare.Shared.DTOs;

public sealed class OllamaGpuStatusDto
{
    public string ModelName { get; init; } = string.Empty;
    public bool IsGpu { get; init; }
    public string? DeviceName { get; init; }
}