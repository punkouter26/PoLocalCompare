using PoLocalCompare.Shared.Enums;

namespace PoLocalCompare.Client.Components;

/// <summary>
/// Single source of truth for how models are grouped by type across the app, so the
/// Model Health panel and the Home model picker present identical Remote / Browser /
/// Ollama groups in the same order.
/// </summary>
public static class ModelTypeGroup
{
    /// <summary>Display order: Remote → Browser → Ollama (matches the picker's filter chips).</summary>
    public static int Order(ModelType type) => type switch
    {
        ModelType.Remote => 0,
        ModelType.Local => 1,          // browser / WebLLM
        ModelType.LocalService => 2,   // Ollama
        _ => 9,
    };

    /// <summary>Human label with icon, e.g. "☁ Remote".</summary>
    public static string Label(ModelType type) => type switch
    {
        ModelType.Remote => "☁ Remote",
        ModelType.Local => "🖥 Browser",
        ModelType.LocalService => "🦙 Ollama",
        _ => "Other",
    };
}
