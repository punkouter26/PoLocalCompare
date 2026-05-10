using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Application.Duels.CommenceDuel;
using PoLocalCompare.Application.Duels.GetDuel;
using PoLocalCompare.Application.Duels.RecordVerdict;
using PoLocalCompare.Api.Services;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Endpoints;

public static class DuelsEndpoints
{
    public static IEndpointRouteBuilder MapDuelsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/duels").WithTags("Duels");

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
            [FromServices] IDuelResultRepository duelResultRepo) =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelId))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["ModelId"] = ["ModelId is required."]
                });

            var result = new DuelResult(duelId, request.ModelId)
            {
                HtmlOutputRaw = request.HtmlOutputRaw ?? string.Empty,
                HtmlOutputSizeBytes = System.Text.Encoding.UTF8.GetByteCount(request.HtmlOutputRaw ?? string.Empty),
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

            await duelResultRepo.SaveAsync(result);

            return Results.Ok();
        })
        .WithName("PostLocalResult")
        .WithSummary("Receives the HTML output from a client-side (WebLLM) local model inference.")
        .Produces(StatusCodes.Status200OK)
        .ProducesValidationProblem();

        // T063 — POST /api/duels/{duelId}/verdict
        group.MapPost("/{duelId}/verdict", async (
            string duelId,
            [FromBody] VerdictRequestDto request,
            [FromServices] RecordVerdictHandler handler) =>
        {
            try
            {
                var response = await handler.HandleAsync(
                    new PoLocalCompare.Application.Duels.RecordVerdict.RecordVerdictCommand(duelId, request.Verdict));

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
