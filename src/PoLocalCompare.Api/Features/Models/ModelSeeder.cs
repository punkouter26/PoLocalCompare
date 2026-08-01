using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Api.Features.Models;

/// <summary>
/// Seeds the default set of models into the Model Registry on first run.
/// Only runs when the Models table is completely empty (idempotent).
/// </summary>
public static class ModelSeeder
{
    private static readonly List<Model> DefaultModels =
    [
        // ── Local WebLLM (in-browser) ──────────────────────────────────────
        new Model(ModelId.From("01SEED0000000000000000001"), "SmolLM2 135M",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-135M-Instruct-q0f32-MLC"),
        new Model(ModelId.From("01SEED0000000000000000002"), "SmolLM2 360M",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-360M-Instruct-q4f32_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000003"), "SmolLM2 1.7B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-1.7B-Instruct-q4f16_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000004"), "Qwen2.5 0.5B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Qwen2.5-0.5B-Instruct-q4f32_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000005"), "Qwen3 1.7B",    ModelType.Local, tdpWatts: 115, webLlmModelId: "Qwen3-1.7B-q4f16_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000006"), "Llama 3.2 1B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Llama-3.2-1B-Instruct-q4f16_1-MLC"),
        new Model(ModelId.From("01SEED0000000000000000009"), "Gemma 2 2B",    ModelType.Local, tdpWatts: 115, webLlmModelId: "gemma-2-2b-it-q4f16_1-MLC"),

        // Ids 007 (Llama 3.2 3B) and 008 (Phi-3.5 Mini) are retired, not reused. Both failed to
        // load in the browser across three independent runs, each on a dedicated cold browser:
        // Llama 3.2 3B loses the GPU device ("A valid external Instance reference no longer
        // exists") and Phi-3.5 Mini aborts with exit(1). Neither is a leak from an earlier model
        // — Llama 3.2 3B failed running first with 11.3 GB of 12 GB VRAM free. Phi-3.5 Mini's
        // 2.1 GB of q4f32 weights exceed the adapter's 2048 MB maxBufferSize, which explains it;
        // Llama 3.2 3B at 1.7 GB sits under every measured limit and remains unexplained.
        // Their weights are still on disk under wwwroot/models/, so re-adding is a one-line
        // change if a future WebGPU or MLC release fixes them.

        // ── Ollama local service ───────────────────────────────────────────
        new Model(ModelId.From("01SEED000000000000000000A"), "Gemma 4 (Ollama)",  ModelType.LocalService, tdpWatts: 115, apiEndpointRef: "gemma4:latest"),
        new Model(ModelId.From("01SEED000000000000000000B"), "Qwen 3.5 (Ollama)", ModelType.LocalService, tdpWatts: 115, apiEndpointRef: "qwen3.5:latest"),

        // ── Azure remote models (po-aiservices-shared) ────────────────────
        // Only deployments that actually exist in the Foundry resource are seeded.
        new Model(ModelId.From("01SEED000000000000000000M"), "GPT-5 Nano",     ModelType.Remote, apiEndpointRef: "gpt-5-nano",                    inputTokenPricePerMillion: 0.05m,  outputTokenPricePerMillion: 0.40m),
        new Model(ModelId.From("01SEED000000000000000000N"), "GPT-5.4 Nano",   ModelType.Remote, apiEndpointRef: "gpt-5.4-nano",                  inputTokenPricePerMillion: 0.20m,  outputTokenPricePerMillion: 1.25m),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<ModelSeederMarker>>();
        var environment = services.GetRequiredService<IHostEnvironment>();

        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();

        var existing = (await repo.GetAllAsync()).ToList();
        if (existing.Count > 0)
        {
            logger.LogInformation("ModelSeeder: {Count} models already registered — skipping seed.", existing.Count);
            return;
        }

        // Ollama (LocalService) models require a local Ollama daemon that does not exist in the
        // cloud — only seed them in Development so the Production catalog has no dead entries.
        var modelsToSeed = environment.IsDevelopment()
            ? DefaultModels
            : DefaultModels.Where(m => m.ModelType != ModelType.LocalService).ToList();

        logger.LogInformation("ModelSeeder: No models found — seeding {Count} default models.", modelsToSeed.Count);

        foreach (var model in modelsToSeed)
        {
            await repo.SaveAsync(model);
            logger.LogInformation("ModelSeeder: Registered '{DisplayName}'.", model.DisplayName);
        }

        logger.LogInformation("ModelSeeder: Seed complete.");
    }
}

file sealed class ModelSeederMarker { }
