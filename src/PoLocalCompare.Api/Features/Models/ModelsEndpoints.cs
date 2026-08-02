using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/models").WithTags("Models").RequireAuthorization();

        group.MapGet("/", async (
            [FromServices] ListModelsHandler handler,
            [FromServices] IWebHostEnvironment env) =>
            Results.Ok(ModelVisibility.Filter(await handler.HandleAsync(), env)))
        .WithName("ListModels")
        .WithSummary("Returns all registered models with current ELO and duel counts.")
        .Produces<IEnumerable<ModelDto>>();

        group.MapGet("/availability", async (
            [FromServices] GetModelAvailabilityHandler handler,
            CancellationToken ct) => Results.Ok(await handler.HandleAsync(ct)))
        .WithName("GetModelAvailability")
        .WithSummary("Returns per-model runtime availability so only confirmed working models can be selected.")
        .Produces<IEnumerable<ModelAvailabilityDto>>();

        group.MapPost("/", async (
            [FromBody] RegisterModelRequest request,
            [FromServices] RegisterModelHandler handler) =>
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["DisplayName"] = ["DisplayName is required."]
                });

            var command = new RegisterModelCommand(
                request.DisplayName,
                request.ModelType,
                request.TdpWatts,
                request.WebLlmModelId,
                request.ApiEndpointRef,
                request.InputTokenPricePerMillion,
                request.OutputTokenPricePerMillion);

            try
            {
                var dto = await handler.HandleAsync(command);
                return Results.Created($"/api/models/{dto.ModelId}", dto);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["Request"] = [ex.Message]
                });
            }
        })
        .WithName("RegisterModel")
        .WithSummary("Registers a new model in the Model Registry.")
        .Produces<ModelDto>(StatusCodes.Status201Created)
        .ProducesValidationProblem();

        group.MapDelete("/{modelId}", async (
            [FromRoute] ModelId modelId,
            [FromServices] IModelRepository repository) =>
        {
            var model = await repository.GetByIdAsync(modelId);
            if (model is null) return Results.NotFound();
            await repository.DeleteAsync(modelId);
            return Results.NoContent();
        })
        .WithName("DeleteModel")
        .WithSummary("Removes a model from the registry.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{webLlmModelId}/download", async (
            [FromRoute] string webLlmModelId,
            [FromServices] DownloadModelHandler handler) =>
            await handler.HandleAsync(webLlmModelId) switch
            {
                DownloadModelHandler.Outcome.InvalidId =>
                    Results.BadRequest(new { error = "Invalid model ID format." }),
                DownloadModelHandler.Outcome.UnknownModel =>
                    Results.NotFound(new { error = $"No registered model with WebLlmModelId '{webLlmModelId}'." }),
                DownloadModelHandler.Outcome.ScriptMissing =>
                    Results.Problem("SCRIPTS/download-models.py not found. Run from repo root.",
                        statusCode: StatusCodes.Status500InternalServerError),
                _ => Results.Accepted(value: new
                {
                    webLlmModelId,
                    status = "Downloading in background. Refresh status in a few minutes."
                })
            })
        .WithName("DownloadModel")
        .WithSummary("Triggers background download of a local WebLLM model from HuggingFace.")
        .Produces(StatusCodes.Status202Accepted)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        return app;
    }
}

public sealed record RegisterModelRequest(
    string DisplayName,
    ModelType ModelType,
    double? TdpWatts,
    string? WebLlmModelId,
    string? ApiEndpointRef,
    decimal? InputTokenPricePerMillion,
    decimal? OutputTokenPricePerMillion);
