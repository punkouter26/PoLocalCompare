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
                var dto = await handler.HandleAsync(new CommenceDuelCommand(
                    request.LeftModelId,
                    request.RightModelId,
                    request.PromptText));

                await executionService.EnqueueAsync(dto.DuelId);

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
            string duelId,
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
            string duelId,
            [FromBody] LocalResultRequest request,
            [FromServices] IDuelResultRepository duelResultRepo,
            [FromServices] IModelRepository modelRepo,
            [FromServices] IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelId))
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
            string duelId,
            [FromBody] VerdictRequestDto request,
            [FromServices] RecordVerdictHandler handler,
            [FromServices] HybridCache cache) =>
        {
            try
            {
                VerdictResponseDto? response;
                try
                {
                    response = await handler.HandleAsync(new RecordVerdictCommand(duelId, request.Verdict));
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    // Lost an optimistic-concurrency race (standards §5.5) — the handler re-reads
                    // everything, so one retry resolves against the fresh state.
                    response = await handler.HandleAsync(new RecordVerdictCommand(duelId, request.Verdict));
                }

                if (response is null)
                    return Results.NotFound(new { error = $"Duel '{duelId}' not found." });

                await cache.RemoveByTagAsync(LeaderboardEndpoints.LeaderboardCacheTag);
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

        // POST /api/duels/{duelId}/auto-judge — GPT-4.1 Nano judges when user doesn’t pick within timeout
        group.MapPost("/{duelId}/auto-judge", async (
            string duelId,
            [FromServices] AutoJudgeService autoJudgeService,
            [FromServices] HybridCache cache,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var response = await autoJudgeService.JudgeAsync(duelId, cancellationToken);
                if (response is null)
                    return Results.NotFound(new { error = $"Duel '{duelId}' not found." });
                await cache.RemoveByTagAsync(LeaderboardEndpoints.LeaderboardCacheTag, cancellationToken);
                return Results.Ok(response);
            }
            catch (AutoJudgeUnavailableException ex)
            {
                // Judge model unreachable — duel left Pending; client falls back to manual judging.
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
        .WithName("AutoJudgeDuel")
        .WithSummary("Uses GPT-4.1 Nano to automatically judge the duel winner when no human verdict is given.")
        .Produces<VerdictResponseDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound)
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status503ServiceUnavailable);

        // T077 — GET /api/duels (archive listing with pagination)
        group.MapGet("/", async (
            [FromQuery] int? limit,
            [FromQuery] string? before,
            [FromServices] ListDuelsHandler handler) =>
        {
            // `limit` is optional: an absent (or 0) value defaults to 20. Declaring it
            // non-nullable/required previously returned 500 when callers omitted it.
            var clampedLimit = Math.Clamp(limit is null or 0 ? 20 : limit.Value, 1, 100);
            var results = await handler.HandleAsync(new ListDuelsQuery(clampedLimit, before));
            return Results.Ok(results);
        })
        .WithName("ListDuels")
        .WithSummary("Lists duel summaries in reverse chronological order.")
        .Produces<IReadOnlyList<DuelSummaryDto>>(StatusCodes.Status200OK);

        // T081 — GET /api/duels/{duelId}/report (Lab Report export)
        group.MapGet("/{duelId}/report", async (
            string duelId,
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
}

public sealed record CommenceDuelRequest(
    string LeftModelId,
    string RightModelId,
    string PromptText);

public sealed record LocalResultRequest(
    string ModelId,
    string? HtmlOutputRaw,
    int TokenCount,
    long TotalDurationMs,
    long WarmUpDurationMs,
    bool IsFailure,
    string? FailureReason);
