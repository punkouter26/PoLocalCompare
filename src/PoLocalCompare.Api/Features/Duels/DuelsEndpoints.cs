using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Azure;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Duels;

public static class DuelsEndpoints
{
    public static IEndpointRouteBuilder MapDuelsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/duels").WithTags("Duels").RequireAuthorization();

        group.MapPost("/", async (
            [FromBody] CommenceDuelRequest request,
            [FromServices] CommenceDuelHandler handler,
            [FromServices] DuelExecutionService executionService) =>
        {
            if (string.IsNullOrWhiteSpace(request.PromptText))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["PromptText"] = ["PromptText is required."]
                });

            try
            {
                // Clamped to the same range AutoJudge itself enforces, so a caller cannot park a
                // duel's judge an hour out. Passing 0 only skips the asker's own grace window.
                var delayOverride = request.AutoJudgeDelaySeconds is { } requested
                    ? Math.Clamp(requested, 0, 3600)
                    : (int?)null;

                var dto = await handler.HandleAsync(new CommenceDuelCommand(
                    request.LeftModelId,
                    request.RightModelId,
                    request.PromptText,
                    delayOverride));

                await executionService.EnqueueAsync(dto.DuelId, delayOverride);

                return Results.Accepted($"/api/duels/{dto.DuelId}", dto);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Request"] = [ex.Message]
                });
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return Results.Problem(
                    title: "Storage not ready",
                    detail: "Duel storage is initializing. Please retry in a few seconds.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
        .WithName("CommenceDuel")
        .WithSummary("Starts a new duel between two models.")
        .Produces<DuelDto>(StatusCodes.Status202Accepted)
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{duelId}", async (
            DuelId duelId,
            [FromServices] GetDuelHandler handler) =>
        {
            var dto = await handler.HandleAsync(new GetDuelQuery(duelId));
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithName("GetDuel")
        .WithSummary("Gets a duel by ID including full telemetry results.")
        .Produces<DuelDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);

        // Called by the Blazor client after local (WebLLM) inference completes
        group.MapPost("/{duelId}/local-result", async (
            DuelId duelId,
            [FromBody] LocalResultRequest request,
            [FromServices] IDuelResultRepository duelResultRepo,
            [FromServices] IModelRepository modelRepo,
            [FromServices] IConfiguration configuration) =>
        {
            if (request.ModelId.IsEmpty)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ModelId"] = ["ModelId is required."]
                });

            var normalizedHtml = HtmlOutputNormalizer.Normalize(request.HtmlOutputRaw);

            var result = new DuelResult(duelId, request.ModelId)
            {
                HtmlOutputRaw = normalizedHtml,
                HtmlOutputSizeBytes = System.Text.Encoding.UTF8.GetByteCount(normalizedHtml),
                TokenCount = request.TokenCount,
                TotalDurationMs = request.TotalDurationMs,
                WarmUpDurationMs = request.WarmUpDurationMs,
                GenerationDurationMs = request.TotalDurationMs - request.WarmUpDurationMs,
                TokenVelocity = request.TotalDurationMs > 0
                    ? request.TokenCount / (request.TotalDurationMs / 1000.0)
                    : 0,
                IsFailure = request.IsFailure,
                FailureReason = request.FailureReason,
            };

            // Apply the shared Domain enrichment policy (density, quality, GreenStats) when the model is known.
            var model = await modelRepo.GetByIdAsync(request.ModelId);
            if (model is not null)
            {
                var electricityRate = configuration.GetValue("GreenStats:ElectricityRateUsd", 0.12);
                DuelResultEnricher.Enrich(result, model, electricityRate);
            }

            await duelResultRepo.SaveAsync(result);

            return Results.Ok();
        })
        .WithName("PostLocalResult")
        .WithSummary("Receives the HTML output from a client-side (WebLLM) local model inference.")
        .Produces(StatusCodes.Status200OK)
        .ProducesValidationProblem();

        // POST /api/duels/{duelId}/verdict
        group.MapPost("/{duelId}/verdict", async (
            DuelId duelId,
            [FromBody] VerdictRequestDto request,
            [FromServices] RecordVerdictHandler handler) =>
        {
            try
            {
                var response = await handler.HandleWithRetryAsync(
                    new RecordVerdictCommand(duelId, request.Verdict));

                if (response is null)
                    return Results.NotFound(new { error = $"Duel '{duelId}' not found." });

                return Results.Ok(response);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.UnprocessableEntity(new { error = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        })
        .WithName("RecordVerdict")
        .WithSummary("Records the winner verdict and updates ELO ratings.")
        .Produces<VerdictResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status422UnprocessableEntity);

        // T077 — GET /api/duels (archive listing with pagination)
        group.MapGet("/", async (
            [FromQuery] int? limit,
            [FromQuery] string? before,
            [FromServices] ListDuelsHandler handler) =>
        {
            // `limit` is optional: an absent (or 0) value defaults to 20. Declaring it
            // non-nullable/required previously returned 500 when callers omitted it.
            var clampedLimit = Math.Clamp(limit is null or 0 ? 20 : limit.Value, 1, 100);

            // `before` is a partition cursor, so it is only ever a yyyyMM stamp. The repository
            // binds it as an escaped literal regardless; rejecting the wrong shape here keeps a
            // malformed cursor from silently paging through nothing.
            if (before is not null && !IsMonthStamp(before))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["before"] = ["Must be a yyyyMM month stamp, e.g. 202608."]
                });

            var results = await handler.HandleAsync(new ListDuelsQuery(clampedLimit, before));
            return Results.Ok(results);
        })
        .WithName("ListDuels")
        .WithSummary("Lists duel summaries in reverse chronological order.")
        .Produces<IReadOnlyList<DuelSummaryDto>>(StatusCodes.Status200OK)
        .ProducesValidationProblem();

        // GET /api/duels/demo-plan — the schedule for an unattended demo run.
        // Read-only: it resolves pairings and prompts but writes nothing. The client starts each
        // duel through the ordinary POST /api/duels, so demo duels are real duels.
        group.MapGet("/demo-plan", async (
            [FromQuery] int? rounds,
            [FromQuery] int? seed,
            [FromServices] DemoPlanHandler handler) =>
        {
            var plan = await handler.HandleAsync(rounds ?? 0, seed);
            return Results.Ok(plan);
        })
        .WithName("GetDemoPlan")
        .WithSummary("Returns the pairings and prompts for a self-running demo.")
        .Produces<DemoPlanDto>(StatusCodes.Status200OK);

        // T081 — GET /api/duels/{duelId}/report (Lab Report export)
        group.MapGet("/{duelId}/report", async (
            DuelId duelId,
            HttpContext httpContext,
            [FromServices] ExportLabReportHandler handler) =>
        {
            var html = await handler.HandleAsync(new ExportLabReportCommand(duelId));
            if (html is null)
                return Results.NotFound(new { error = $"Duel '{duelId}' not found." });

            httpContext.Response.Headers["Content-Disposition"] = $"attachment; filename=\"lab-report-{duelId}.html\"";
            return Results.Content(
                html,
                contentType: "text/html",
                contentEncoding: System.Text.Encoding.UTF8,
                statusCode: StatusCodes.Status200OK);
        })
        .WithName("ExportLabReport")
        .WithSummary("Exports a self-contained HTML Lab Report for the specified duel.")
        .Produces<string>(StatusCodes.Status200OK, contentType: "text/html")
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    /// <summary>True for a bare <c>yyyyMM</c> stamp — the shape duel partition keys use.</summary>
    private static bool IsMonthStamp(string value)
    {
        if (value.Length != 6) return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }

        var month = int.Parse(value.AsSpan(4, 2));
        return month is >= 1 and <= 12;
    }
}

/// <param name="AutoJudgeDelaySeconds">
/// Optional per-duel grace window. Omit to use <c>AiJudge:DelaySeconds</c>; demo mode sends 0
/// so an unattended run is never waiting on a human pick that will not come.
/// </param>
public sealed record CommenceDuelRequest(
    ModelId LeftModelId,
    ModelId RightModelId,
    string PromptText,
    int? AutoJudgeDelaySeconds = null);

public sealed record LocalResultRequest(
    ModelId ModelId,
    string? HtmlOutputRaw,
    int TokenCount,
    long TotalDurationMs,
    long WarmUpDurationMs,
    bool IsFailure,
    string? FailureReason);
