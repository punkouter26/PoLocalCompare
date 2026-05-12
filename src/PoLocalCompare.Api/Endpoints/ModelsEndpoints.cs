using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Application.Models.ListModels;
using PoLocalCompare.Application.Models.RegisterModel;
using PoLocalCompare.Shared.DTOs;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Endpoints;

public static class ModelsEndpoints
{
    public static IEndpointRouteBuilder MapModelsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/models").WithTags("Models");

        group.MapGet("/", async (
            [FromServices] ListModelsHandler handler) =>
        {
            var models = await handler.HandleAsync(new ListModelsQuery());
            return Results.Ok(models);
        })
        .WithName("ListModels")
        .WithSummary("Returns all registered models with current ELO and duel counts.")
        .Produces<IEnumerable<ModelDto>>();

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

        group.MapGet("/download-status/{webLlmModelId}", (
            [FromRoute] string webLlmModelId,
            [FromServices] IWebHostEnvironment env) =>
        {
            if (string.IsNullOrWhiteSpace(webLlmModelId))
            {
                return Results.BadRequest(new { error = "webLlmModelId is required." });
            }

            if (webLlmModelId.Contains("..", StringComparison.Ordinal) ||
                webLlmModelId.Contains('/', StringComparison.Ordinal) ||
                webLlmModelId.Contains('\\', StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "Invalid model id." });
            }

            var relativePath = $"models/{webLlmModelId}/mlc-chat-config.json";
            var fileInfo = env.WebRootFileProvider.GetFileInfo(relativePath);
            var downloaded = fileInfo.Exists && fileInfo.Length > 0;
            return Results.Ok(new ModelDownloadStatusDto(webLlmModelId, downloaded));
        })
        .WithName("GetModelDownloadStatus")
        .WithSummary("Checks whether a local WebLLM model asset has been downloaded.")
        .Produces<ModelDownloadStatusDto>();

        group.MapPatch("/{modelId}", async (
            [FromRoute] string modelId,
            [FromBody] PatchModelRequest request,
            [FromServices] IModelRepository repository) =>
        {
            var model = await repository.GetByIdAsync(modelId);
            if (model is null) return Results.NotFound();

            var updated = new PoLocalCompare.Domain.Entities.Model
            {
                ModelId = model.ModelId,
                DisplayName = request.DisplayName ?? model.DisplayName,
                ModelType = model.ModelType,
                CurrentElo = model.CurrentElo,
                DuelCount = model.DuelCount,
                WinCount = model.WinCount,
                GreenScoreAvg = model.GreenScoreAvg,
                TdpWatts = model.TdpWatts,
                WebLlmModelId = model.WebLlmModelId,
                ApiEndpointRef = request.ApiEndpointRef ?? model.ApiEndpointRef,
                InputTokenPricePerMillion = model.InputTokenPricePerMillion,
                OutputTokenPricePerMillion = model.OutputTokenPricePerMillion,
                CreatedAt = model.CreatedAt
            };

            await repository.UpdateAsync(updated);

            var dto = new PoLocalCompare.Shared.DTOs.ModelDto
            {
                ModelId = updated.ModelId,
                DisplayName = updated.DisplayName,
                ModelType = updated.ModelType,
                CurrentElo = updated.CurrentElo,
                DuelCount = updated.DuelCount,
                WinCount = updated.WinCount,
                GreenScoreAvg = updated.GreenScoreAvg,
                TdpWatts = updated.TdpWatts,
                WebLlmModelId = updated.WebLlmModelId,
                ApiEndpointRef = updated.ApiEndpointRef,
                InputTokenPricePerMillion = updated.InputTokenPricePerMillion,
                OutputTokenPricePerMillion = updated.OutputTokenPricePerMillion,
                CreatedAt = updated.CreatedAt
            };
            return Results.Ok(dto);
        })
        .WithName("PatchModel")
        .WithSummary("Updates DisplayName and/or ApiEndpointRef for an existing model.")
        .Produces<PoLocalCompare.Shared.DTOs.ModelDto>()
        .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{modelId}", async (
            [FromRoute] string modelId,
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

public sealed record PatchModelRequest(
    string? DisplayName,
    string? ApiEndpointRef);

public sealed record ModelDownloadStatusDto(
    string WebLlmModelId,
    bool Downloaded);
