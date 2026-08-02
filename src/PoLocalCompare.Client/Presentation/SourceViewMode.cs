namespace PoLocalCompare.Client.Presentation;

/// <summary>How the Arena is presenting the two outputs.</summary>
public enum SourceViewMode
{
    /// <summary>The sandboxed iframes — what the models actually built.</summary>
    Rendered,

    /// <summary>Both sources side by side, scroll-linked.</summary>
    Code,

    /// <summary>Aligned line diff, identical stretches folded away.</summary>
    Diff,
}
