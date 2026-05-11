using Microsoft.AspNetCore.Mvc;
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
