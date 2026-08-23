namespace PoLocalCompare.Shared.Presentation;

/// <summary>
/// Presentational strings for the model's overall-readiness badge that appears in the
/// Home header (and any future surface that needs the same "models ready / remote offline"
/// indicator). Centralised so the label, the colour-modifier class and the tooltip never
/// disagree about what is being shown.
/// </summary>
/// <remarks>
/// Three concerns, one place: a label, a CSS modifier class, and a tooltip. They were a
/// private trio of statics on Home.razor — fine until another surface (NavMenu's mock-data
/// banner, the model-health panel's status line) wanted the same shape and would have had
/// to fork the trio. They live here so every consumer renders the same badge.
/// </remarks>
public static class HealthBadgeText
{
    /// <summary>The short status word + emoji that lands in the badge body.</summary>
    public static string Label(bool remoteHealthy) =>
        remoteHealthy ? "🟢 Models ready" : "🟡 Remote offline";

    /// <summary>BEM modifier class that picks the colour palette — pair with `home__health`.</summary>
    public static string ModifierClass(bool remoteHealthy) =>
        remoteHealthy ? "home__health--ok" : "home__health--warn";

    /// <summary>Verbose tooltip explaining what the badge actually means right now.</summary>
    /// <param name="ready">Count of models the per-model availability probe says are usable.</param>
    /// <param name="total">Total models in the catalog, ready or not.</param>
    /// <param name="cloudMode">True when the app is running in Cloud Mode (no Foundry endpoint).</param>
    public static string Tooltip(int? ready, int total, bool? cloudMode)
    {
        if (cloudMode == true)
            return "Cloud mode: only local models will work right now.";

        if (ready is { } readyCount && readyCount > 0)
            return $"{readyCount} of {total} models ready";

        return $"{total} models available";
    }
}
