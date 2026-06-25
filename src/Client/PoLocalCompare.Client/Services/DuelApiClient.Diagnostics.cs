using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Client.Services;

public sealed partial class DuelApiClient
{
    public async Task<IReadOnlyList<OllamaGpuStatusDto>> GetOllamaGpuStatusAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<IReadOnlyList<OllamaGpuStatusDto>>(
                "/api/ollama/gpu-status", JsonOptions)
                ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama GPU status unavailable");
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> GetOllamaAvailableModelsAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<IReadOnlyList<string>>(
                "/api/ollama/available-models", JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ollama available-models unavailable");
            return [];
        }
    }

    public async Task<OllamaBenchmarkResultDto?> BenchmarkOllamaModelAsync(string modelName, string prompt)
    {
        try
        {
            var body = new { modelName, prompt };
            var response = await _http.PostAsJsonAsync("/api/ollama/benchmark", body, JsonOptions);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<OllamaBenchmarkResultDto>(JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ollama benchmark failed for model {Model}", modelName);
            return null;
        }
    }

    public async Task<HealthSnapshotDto?> GetHealthSnapshotAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<HealthSnapshotDto>("/health", JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health snapshot fetch failed");
            return null;
        }
    }

    public async Task<SmokeSnapshotDto?> GetSmokeSnapshotAsync()
    {
        try
        {
            return await _http.GetFromJsonAsync<SmokeSnapshotDto>("/api/diag/smoke", JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Smoke snapshot fetch failed");
            return null;
        }
    }

    public async Task<DiagnosticsWarningsDto?> GetDiagnosticsWarningsAsync(int limit = 5)
    {
        try
        {
            return await _http.GetFromJsonAsync<DiagnosticsWarningsDto>($"/api/diag/warnings?limit={limit}", JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostics warnings fetch failed");
            return null;
        }
    }

    public async Task DevResetAsync()
    {
        var response = await _http.PostAsync("/api/dev/reset", null);
        response.EnsureSuccessStatusCode();
    }

    public sealed class HealthSnapshotDto
    {
        public string Status { get; set; } = "Unknown";
    }

    public sealed class SmokeSnapshotDto
    {
        public string Status { get; set; } = "Unknown";
        public string Environment { get; set; } = "Unknown";
        public SmokeModelsDto Models { get; set; } = new();
    }

    public sealed class SmokeModelsDto
    {
        public int Total { get; set; }
        public int LocalService { get; set; }
        public bool CloudMode { get; set; }
    }

    public sealed class DiagnosticsWarningsDto
    {
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public IReadOnlyList<DiagnosticsWarningEntryDto> Entries { get; set; } = [];
    }

    public sealed class DiagnosticsWarningEntryDto
    {
        public string Level { get; set; } = "Warning";
        public string Timestamp { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }
}