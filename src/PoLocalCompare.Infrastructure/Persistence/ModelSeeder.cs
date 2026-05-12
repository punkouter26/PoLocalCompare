using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoLocalCompare.Application.Interfaces;
using PoLocalCompare.Domain.Entities;
using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Infrastructure.Persistence;

/// <summary>
/// Seeds the default set of models into the Model Registry on first run.
/// Only runs when the Models table is completely empty (idempotent).
/// </summary>
public static class ModelSeeder
{
    private static readonly List<Model> DefaultModels =
    [
        // ── Local WebLLM (in-browser) ──────────────────────────────────────
        new Model("01SEED0000000000000000001", "SmolLM2 135M",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-135M-Instruct-q0f32-MLC"),
        new Model("01SEED0000000000000000002", "SmolLM2 360M",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-360M-Instruct-q4f32_1-MLC"),
        new Model("01SEED0000000000000000003", "SmolLM2 1.7B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "SmolLM2-1.7B-Instruct-q4f16_1-MLC"),
        new Model("01SEED0000000000000000004", "Qwen2.5 0.5B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Qwen2.5-0.5B-Instruct-q4f32_1-MLC"),
        new Model("01SEED0000000000000000005", "Qwen3 1.7B",    ModelType.Local, tdpWatts: 115, webLlmModelId: "Qwen3-1.7B-q4f16_1-MLC"),
        new Model("01SEED0000000000000000006", "Llama 3.2 1B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Llama-3.2-1B-Instruct-q4f16_1-MLC"),
        new Model("01SEED0000000000000000007", "Llama 3.2 3B",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Llama-3.2-3B-Instruct-q4f16_1-MLC"),
        new Model("01SEED0000000000000000008", "Phi-3.5 Mini",  ModelType.Local, tdpWatts: 115, webLlmModelId: "Phi-3.5-mini-instruct-q4f32_1-MLC"),
        new Model("01SEED0000000000000000009", "Gemma 2 2B",    ModelType.Local, tdpWatts: 115, webLlmModelId: "gemma-2-2b-it-q4f16_1-MLC"),

        // ── Ollama local service ───────────────────────────────────────────
        new Model("01SEED000000000000000000A", "Gemma 4 (Ollama)",  ModelType.LocalService, tdpWatts: 115, apiEndpointRef: "gemma4:latest"),
        new Model("01SEED000000000000000000B", "Qwen 3.5 (Ollama)", ModelType.LocalService, tdpWatts: 115, apiEndpointRef: "qwen3.5:latest"),

        // ── Azure AI Foundry remote models ────────────────────────────────
        new Model("01SEED000000000000000000C", "GPT-4o",        ModelType.Remote, apiEndpointRef: "gpt-4o",              inputTokenPricePerMillion: 2.5m,  outputTokenPricePerMillion: 10.0m),
        new Model("01SEED000000000000000000D", "GPT-4.1 Mini",  ModelType.Remote, apiEndpointRef: "gpt-4.1-mini",        inputTokenPricePerMillion: 0.4m,  outputTokenPricePerMillion: 1.6m),
        new Model("01SEED000000000000000000E", "GPT-4.1 Nano",  ModelType.Remote, apiEndpointRef: "gpt-4.1-nano",        inputTokenPricePerMillion: 0.1m,  outputTokenPricePerMillion: 0.4m),
        new Model("01SEED000000000000000000F", "Phi-4 Mini",    ModelType.Remote, apiEndpointRef: "Phi-4-mini-instruct", inputTokenPricePerMillion: 0.07m, outputTokenPricePerMillion: 0.28m),
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        var logger = services.GetRequiredService<ILogger<ModelSeederMarker>>();

        using var scope = services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IModelRepository>();

        var existing = (await repo.GetAllAsync()).ToList();
        if (existing.Count > 0)
        {
            logger.LogInformation("ModelSeeder: {Count} models already registered — skipping seed.", existing.Count);
            return;
        }

        logger.LogInformation("ModelSeeder: No models found — seeding {Count} default models.", DefaultModels.Count);

        foreach (var model in DefaultModels)
        {
            await repo.SaveAsync(model);
            logger.LogInformation("ModelSeeder: Registered '{DisplayName}'.", model.DisplayName);
        }

        logger.LogInformation("ModelSeeder: Seed complete.");
    }
}

file sealed class ModelSeederMarker { }
