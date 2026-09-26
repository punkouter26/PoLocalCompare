using Microsoft.Extensions.DependencyInjection;
using PoLocalCompare.Api.Features.Models;
using PoLocalCompare.Shared.Enums;
using PoLocalCompare.Shared.Ids;

namespace PoLocalCompare.Integration;

/// <summary>
/// Seeds the registry through the repository. There is no public "register any model" endpoint —
/// the UI only adds models via /api/models/discovered, which re-checks them against the WebLLM
/// bundle or the Ollama daemon — so tests write the row directly.
/// </summary>
internal static class TestModels
{
    public static Task<string> RemoteAsync(IServiceProvider services, string name) =>
        SaveAsync(services, new Model(ModelId.New(), name, ModelType.Remote,
            apiEndpointRef: "test-deployment", inputTokenPricePerMillion: 1.0m, outputTokenPricePerMillion: 3.0m));

    /// <summary>A browser (WebLLM) model. The WebLLM id is derived from the name so two never collide.</summary>
    public static Task<string> LocalAsync(IServiceProvider services, string name) =>
        SaveAsync(services, new Model(ModelId.New(), name, ModelType.Local,
            tdpWatts: 45.0, webLlmModelId: $"test-webllm-{name.Replace(' ', '-').ToLowerInvariant()}"));

    private static async Task<string> SaveAsync(IServiceProvider services, Model model)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IModelRepository>().SaveAsync(model);
        return model.ModelId.Value;
    }
}
