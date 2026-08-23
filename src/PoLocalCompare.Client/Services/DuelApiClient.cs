using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Client.Services;

/// <summary>
/// The client's whole view of the API. Every call goes through the same
/// <see cref="JsonOptions"/> so enum casing cannot drift between endpoints.
///
/// Kept as one type: this was five partial files totalling ~230 lines, which cost five
/// using-blocks and five namespace declarations to save nothing. Comment banners mark
/// the slices.
/// </summary>
public sealed class DuelApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DuelApiClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DuelApiClient(HttpClient http, ILogger<DuelApiClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ── Duels ────────────────────────────────────────────────────────────────

    /// <param name="autoJudgeDelaySeconds">
    /// Per-duel grace window before the AI judge decides. Null keeps the server's configured
    /// value; demo mode passes 0 so an unattended run never stalls waiting for a human pick.
    /// </param>
/// <param name="challengeKind">
    /// Budget the duel is fought under. <see cref="ChallengeKind.None"/> — the default — is an
    /// ordinary duel, so existing callers are unaffected.
    /// </param>
    public async Task<DuelDto?> CommenceDuelAsync(
        string leftModelId,
        string rightModelId,
        string promptText,
        int? autoJudgeDelaySeconds = null,
        ChallengeKind challengeKind = ChallengeKind.None,
        double challengeThreshold = 0)
    {
        var body = new
        {
            leftModelId,
            rightModelId,
            promptText,
            autoJudgeDelaySeconds,
            challengeKind,
            challengeThreshold,
        };
        var response = await _http.PostAsJsonAsync("/api/duels", body);
        // Surface the validation body rather than the raw `net_http_message_not_success…` reason
        // string. The endpoint returns RFC 7807 ProblemDetails; the per-field message is what the
        // user actually needs to read (e.g. "PromptText must be at least 10 characters.").
        if (!response.IsSuccessStatusCode)
        {
            var problem = await TryReadProblemAsync(response);
            throw new DuelStartException(response.StatusCode, problem);
        }
        return await response.Content.ReadFromJsonAsync<DuelDto>(JsonOptions);
    }

    /// <summary>
    /// Best-effort parse of an RFC 7807 ProblemDetails body. Returns null if the body is empty
    /// or not in the expected shape, so callers fall back to the status code's reason phrase.
    /// </summary>
    private static async Task<ProblemDetailsLite?> TryReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemDetailsLite>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Subset of <c>Microsoft.AspNetCore.Mvc.ProblemDetails</c> we care about.</summary>
    public sealed class ProblemDetailsLite
    {
        public string? Title { get; init; }
        public string? Detail { get; init; }
        // ASP.NET's ValidationProblemDetails nests per-field errors here.
        public Dictionary<string, string[]>? Errors { get; init; }

        /// <summary>
        /// First non-empty field error if present (this is what the API surfaces for the
        /// PromptText length check), else <see cref="Detail"/>, else <see cref="Title"/>.
        /// </summary>
        public string? FirstMessage()
        {
            if (Errors is { Count: > 0 })
            {
                var first = Errors.Values.SelectMany(v => v).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));
                if (first is not null) return first;
            }
            return Detail ?? Title;
        }
    }

    /// <summary>
    /// Thrown by <see cref="CommenceDuelAsync"/> when the server rejects the request. The
    /// message comes from the parsed <c>ProblemDetails</c> body so the user sees the actual
    /// reason — "PromptText must be at least 10 characters." — rather than the framework's
    /// "BadRequest" reason phrase.
    /// </summary>
    public sealed class DuelStartException : Exception
    {
        public System.Net.HttpStatusCode StatusCode { get; }

        public DuelStartException(System.Net.HttpStatusCode statusCode, ProblemDetailsLite? problem)
            : base(problem?.FirstMessage() ?? $"{statusCode} ({(int)statusCode})")
        {
            StatusCode = statusCode;
        }
    }

    public async Task<DemoPlanDto?> GetDemoPlanAsync(int rounds = 10)
    {
        return await _http.GetFromJsonAsync<DemoPlanDto>($"/api/duels/demo-plan?rounds={rounds}", JsonOptions);
    }

    public async Task<VerdictResponseDto?> RecordVerdictAsync(DuelId duelId, VerdictRequestDto request)
    {
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/verdict", request, JsonOptions);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VerdictResponseDto>(JsonOptions);
    }

    /// <summary>
    /// Reads one duel, or null when the id names nothing.
    /// </summary>
    /// <remarks>
    /// Uses <c>GetAsync</c> rather than <c>GetFromJsonAsync</c> so a 404 becomes the null this
    /// signature already promises. The old version threw, which escaped past the Arena's
    /// "Duel not found" branch to the ErrorBoundary — a mistyped or deleted duel id rendered a
    /// raw <c>HttpRequestException</c> stack instead of a message. This is also the polling
    /// path used while awaiting a verdict, where a transient failure must not tear the page down.
    /// </remarks>
    public async Task<DuelDto?> GetDuelAsync(DuelId duelId)
    {
        var response = await _http.GetAsync($"/api/duels/{duelId}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DuelDto>(JsonOptions);
    }

    public async Task PostLocalResultAsync(
        DuelId duelId,
        ModelId modelId,
        string htmlOutputRaw,
        int tokenCount,
        long totalDurationMs,
        long warmUpDurationMs,
        bool isFailure = false,
        string? failureReason = null)
    {
        var body = new
        {
            modelId,
            htmlOutputRaw,
            tokenCount,
            totalDurationMs,
            warmUpDurationMs,
            isFailure,
            failureReason
        };
        var response = await _http.PostAsJsonAsync($"/api/duels/{duelId}/local-result", body);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<DuelSummaryDto>?> ListDuelsAsync(int limit = 20, string? before = null)
    {
        var url = $"/api/duels?limit={limit}";
        if (!string.IsNullOrEmpty(before))
            url += $"&before={before}";
        return await _http.GetFromJsonAsync<IReadOnlyList<DuelSummaryDto>>(url, JsonOptions);
    }

    // ── Leaderboard ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LeaderboardEntryDto>?> GetLeaderboardAsync(string sortBy = "Elo")
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<LeaderboardEntryDto>>(
            $"/api/leaderboard?sortBy={Uri.EscapeDataString(sortBy)}", JsonOptions);
    }

    public async Task<IReadOnlyList<HeadToHeadDto>?> GetKillListAsync(ModelId modelId)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<HeadToHeadDto>>(
            $"/api/leaderboard/{modelId}/killlist", JsonOptions);
    }

    /// <summary>
    /// Reads one model's profile, or null when the id names nothing.
    /// </summary>
    /// <remarks>
    /// Uses <c>GetAsync</c> rather than <c>GetFromJsonAsync</c> for the same reason
    /// <see cref="GetDuelAsync"/> does: a retired or mistyped model id must surface as the null
    /// this signature promises, not as an exception that escapes to the ErrorBoundary.
    /// </remarks>
    public async Task<ModelProfileDto?> GetModelProfileAsync(ModelId modelId)
    {
        var response = await _http.GetAsync($"/api/leaderboard/{modelId}/profile");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModelProfileDto>(JsonOptions);
    }

    // ── Tournaments ──────────────────────────────────────────────────────────

    /// <summary>Models eligible to enter a bracket, strongest first — which is the seeding order.</summary>
    public async Task<IReadOnlyList<TournamentEntrantDto>?> GetTournamentEntrantsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<TournamentEntrantDto>>(
            "/api/tournaments/entrants", JsonOptions);
    }

    /// <summary>
    /// Draws a bracket and starts it running server-side. The response is the drawn bracket, so
    /// the page can paint the whole thing before the first match has finished.
    /// </summary>
    /// <remarks>
    /// Reads the body on a 400 rather than throwing: every rejection here is a message the user
    /// needs (wrong field size, a browser model in the field, a prompt that is too short), and
    /// EnsureSuccessStatusCode would discard all of it.
    /// </remarks>
    public async Task<TournamentDto?> CreateTournamentAsync(IReadOnlyList<ModelId> modelIds, string promptText)
    {
        var body = new { modelIds, promptText };
        var response = await _http.PostAsJsonAsync("/api/tournaments", body, JsonOptions);

        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemShape>(JsonOptions);
            var detail = problem?.Errors?.SelectMany(e => e.Value).FirstOrDefault();
            throw new InvalidOperationException(detail ?? "That bracket could not be drawn.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TournamentDto>(JsonOptions);
    }

    /// <summary>Reads one bracket, or null when the id names nothing.</summary>
    public async Task<TournamentDto?> GetTournamentAsync(TournamentId tournamentId)
    {
        var response = await _http.GetAsync($"/api/tournaments/{tournamentId}");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TournamentDto>(JsonOptions);
    }

    public async Task<IReadOnlyList<TournamentDto>?> ListTournamentsAsync(int limit = 10)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<TournamentDto>>(
            $"/api/tournaments?limit={limit}", JsonOptions);
    }

    // ── Challenges ───────────────────────────────────────────────────────────

    /// <summary>
    /// Models ranked by how reliably they come in under one kind of budget. One kind at a time,
    /// because "best" is seconds for one and dollars for another.
    /// </summary>
    public async Task<IReadOnlyList<ChallengeLeaderboardEntryDto>?> GetChallengeLeaderboardAsync(ChallengeKind kind)
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ChallengeLeaderboardEntryDto>>(
            $"/api/challenges/leaderboard?kind={kind}", JsonOptions);
    }

    // ── Models ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ModelDto>?> GetModelsAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelDto>>("/api/models", JsonOptions);
    }

    public async Task<IReadOnlyList<ModelAvailabilityDto>?> GetModelAvailabilityAsync()
    {
        return await _http.GetFromJsonAsync<IReadOnlyList<ModelAvailabilityDto>>("/api/models/availability", JsonOptions);
    }

    public async Task<bool> RequestModelDownloadAsync(string webLlmModelId)
    {
        try
        {
            var response = await _http.PostAsync(
                $"/api/models/{Uri.EscapeDataString(webLlmModelId)}/download", null);
            return response.StatusCode == System.Net.HttpStatusCode.Accepted;
        }
        catch
        {
            return false;
        }
    }

    // ── Ollama & diagnostics ─────────────────────────────────────────────────

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
}

/// <summary>
/// The RFC 7807 validation-problem shape, only as far as this client reads it.
/// </summary>
/// <remarks>
/// Declared here rather than using <c>ValidationProblemDetails</c>: that type lives in the MVC
/// assembly, which the WebAssembly client does not reference, and one endpoint reading one field
/// does not justify pulling it in.
/// </remarks>
internal sealed class ValidationProblemShape
{
    public Dictionary<string, string[]>? Errors { get; set; }
}
