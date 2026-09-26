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
            [FromServices] IModelRepository repository,
            [FromServices] IWebHostEnvironment env) =>
            Results.Ok(ModelVisibility.Filter((await repository.GetAllAsync()).Select(m => m.ToDto()), env)))
        .WithName("ListModels")
        .WithSummary("Returns all registered models with current ELO and duel counts.")
        .Produces<IEnumerable<ModelDto>>();

        group.MapGet("/availability", async (
            [FromServices] GetModelAvailabilityHandler handler,
            CancellationToken ct) => Results.Ok(await handler.HandleAsync(ct)))
        .WithName("GetModelAvailability")
        .WithSummary("Returns per-model runtime availability so only confirmed working models can be selected.")
        .Produces<IEnumerable<ModelAvailabilityDto>>();

        // ── Runtime discovery ────────────────────────────────────────────────────
        // The catalog page's feed. ModelSeeder only seeds an empty table, so without these
        // a new model meant editing the seeder and wiping Azurite.
        group.MapGet("/discover", async (
            [FromServices] DiscoverModelsHandler handler,
            CancellationToken ct) => Results.Ok(await handler.HandleAsync(ct)))
        .WithName("DiscoverModels")
        .WithSummary("Lists browser (WebLLM bundle) and Ollama models that can be added and are not yet registered.")
        .Produces<ModelDiscoveryDto>();

        group.MapPost("/discovered", async (
            [FromBody] AddDiscoveredModelRequest request,
            [FromServices] AddDiscoveredModelHandler handler,
            CancellationToken ct) =>
        {
            try
            {
                var dto = await handler.HandleAsync(request, ct);
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
                    ["Id"] = [ex.Message]
                });
            }
        })
        .WithName("AddDiscoveredModel")
        .WithSummary("Registers a model from the discovery list, re-checking that it can actually run.")
        .Produces<ModelDto>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status409Conflict)
        .ProducesValidationProblem();

        // GET /api/models/download-status/{webLlmModelId} — whether the per-model weights directory
        // is present on disk under wwwroot/models/. The JS interop (diag-interop.js, webllm-interop.js)
        // calls this on every page load to skip the CDN probe when the model is already local.
        // A 404 here used to fire for every browser model card on every page (they then fell
        // through to the CDN, so the page still worked, but the network tab was a 404 for every
        // model). The endpoint now returns a structured 200 with downloaded=false so the JS
        // converges to the same CDN path without an error in the log.
        group.MapGet("/download-status/{webLlmModelId}", (
            [FromRoute] string webLlmModelId,
            IWebHostEnvironment environment) =>
        {
            var modelsRoot = Path.Combine(environment.WebRootPath ?? "wwwroot", "models", webLlmModelId);
            var downloaded = Directory.Exists(modelsRoot)
                && Directory.EnumerateFiles(modelsRoot, "mlc-chat-config.json").Any();
            return Results.Ok(new { webLlmModelId, downloaded, localPath = downloaded ? modelsRoot : null });
        })
        .WithName("GetModelDownloadStatus")
        .WithSummary("Reports whether the WebLLM model weights are present locally on disk.")
        .Produces(StatusCodes.Status200OK)
        .AllowAnonymous();

        // There is deliberately no POST /download here. Weights are vendored by
        // SCRIPTS/download-models.py ahead of time (see SCRIPTS/README.md); the endpoint that
        // once kicked that script off in the background had no caller in the UI and was removed
        // on 2026-09-10. Nothing in the client ever invoked it — the JS only ever read
        // download-status above.

        return app;
    }
}
