using Microsoft.AspNetCore.Mvc;
using PoLocalCompare.Shared.DTOs;

namespace PoLocalCompare.Api.Features.Ollama;

public static class OllamaEndpoints
{
    public static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ollama").WithTags("Ollama").RequireAuthorization();

        group.MapGet("/available-models", async (
            [FromServices] ListOllamaModelsHandler handler,
            CancellationToken ct) => Results.Ok(await handler.HandleAsync(ct)))
        .WithName("GetOllamaAvailableModels")
        .WithSummary("Lists all models pulled in the local Ollama instance.")
        .Produces<string[]>();

        group.MapPost("/benchmark", async (
            [FromBody] OllamaBenchmarkRequest request,
            [FromServices] BenchmarkOllamaModelHandler handler,
            CancellationToken ct) => Results.Ok(await handler.HandleAsync(request, ct)))
        .WithName("BenchmarkOllamaModel")
        .WithSummary("Runs a timed inference benchmark on an Ollama model and returns timing stats.")
        .Produces<OllamaBenchmarkResultDto>();

        return app;
    }
}
