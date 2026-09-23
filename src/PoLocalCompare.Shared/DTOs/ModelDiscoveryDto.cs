namespace PoLocalCompare.Shared.DTOs;

/// <summary>
/// Models that could be added to the catalog right now, and why the list looks the way it does.
/// </summary>
/// <remarks>
/// Every candidate here is already known to be runnable: a browser model is listed only if the
/// vendored WebLLM bundle has a <c>prebuiltAppConfig</c> entry for it (the worker cannot load
/// anything else), and an Ollama model only if the local daemon reports it pulled. The Hub
/// metadata is decoration — a missing download count never hides a candidate.
/// </remarks>
public sealed class ModelDiscoveryDto
{
    public IReadOnlyList<DiscoveredBrowserModelDto> Browser { get; init; } = [];
    public IReadOnlyList<DiscoveredOllamaModelDto> Ollama { get; init; } = [];

    /// <summary>False when <c>web-llm.js</c> is missing or is a Git LFS pointer — then no browser model can run at all.</summary>
    public bool BundleAvailable { get; init; }

    /// <summary>False when the Hugging Face Hub could not be reached; candidates are still listed, without popularity.</summary>
    public bool HubReachable { get; init; }

    /// <summary>Ollama models exist in Development only, the same rule the catalog and seeder follow.</summary>
    public bool OllamaSupported { get; init; }
}

public sealed class DiscoveredBrowserModelDto
{
    /// <summary>The <c>model_id</c> in <c>prebuiltAppConfig</c>, stored as the model's <c>WebLlmModelId</c>.</summary>
    public string WebLlmModelId { get; init; } = string.Empty;

    /// <summary>Hub repository the weights come from, e.g. <c>mlc-ai/Qwen2.5-0.5B-Instruct-q4f32_1-MLC</c>.</summary>
    public string? HubRepo { get; init; }
    public string SuggestedName { get; init; } = string.Empty;
    public double? VramRequiredMb { get; init; }
    public bool LowResource { get; init; }
    public long? Downloads { get; init; }
    public long? Likes { get; init; }
}

public sealed class DiscoveredOllamaModelDto
{
    /// <summary>The tag as Ollama names it, e.g. <c>llama3.2:3b</c>; stored as <c>ApiEndpointRef</c>.</summary>
    public string Name { get; init; } = string.Empty;
    public string SuggestedName { get; init; } = string.Empty;
}

public enum DiscoveredModelKind
{
    Browser,
    Ollama,
}

/// <summary>Body of <c>POST /api/models/discovered</c>.</summary>
/// <param name="Id">A <c>WebLlmModelId</c> for a browser model, an Ollama tag otherwise.</param>
/// <param name="DisplayName">Null or blank to take the suggested name.</param>
public sealed record AddDiscoveredModelRequest(DiscoveredModelKind Kind, string Id, string? DisplayName);
