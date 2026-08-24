namespace PoLocalCompare.Api.Common.Inference;

/// <summary>
/// The system prompt every duel contestant gets, whichever path runs it.
/// </summary>
/// <remarks>
/// One constant because there are three runners — the Foundry proxy, the Ollama proxy and
/// <c>wwwroot/js/webllm-worker.js</c> — and a duel is only fair if all three brief the model
/// identically. The worker cannot reference this file, so it carries a copy; the two must be
/// changed together, and the worker's <c>?v=</c> cache-buster bumped, or browser models are
/// answering a different question from the remote ones.
///
/// <para>
/// The HTML-forcing half is load-bearing for small local models, which otherwise answer
/// conversationally ("I'm sorry, but as an AI…") instead of emitting a page.
/// </para>
///
/// <para>
/// The size half exists because the Arena renders each result in a fixed
/// <see cref="PreviewWidth"/>×<see cref="PreviewHeight"/> frame (see <c>--preview-w</c> /
/// <c>--preview-h</c> in <c>SandboxedViewport.razor.css</c>). The frame is a
/// <c>srcdoc</c> iframe with <c>sandbox="allow-scripts"</c> and no <c>allow-same-origin</c>,
/// so the app cannot reach inside it to suppress scrollbars — the only way a result fits
/// without them is for the model to have been told the canvas size up front.
/// </para>
/// </remarks>
public static class InferencePrompt
{
    /// <summary>Width of the Arena preview frame, in CSS pixels.</summary>
    public const int PreviewWidth = 320;

    /// <summary>Height of the Arena preview frame, in CSS pixels.</summary>
    public const int PreviewHeight = 180;

    /// <summary>
    /// Not <c>const</c>: it interpolates the two size constants above so the numbers live in
    /// one place, and C# will not fold an <c>int</c> into a compile-time string.
    /// </summary>
    /// <summary>
    /// Not <c>const</c>: it interpolates the two size constants above so the numbers live in
    /// one place, and C# will not fold an <c>int</c> into a compile-time string. The <c>$$</c>
    /// raw-string prefix makes <c>{{ }}</c> the interpolation delimiter, which leaves the CSS
    /// braces below as literal text.
    /// </summary>
    public static readonly string System =
        $$"""
        You are an expert HTML/CSS coder. Return only valid HTML5 with inline CSS.
        No markdown, no explanation, no code fences.

        The page is displayed in a fixed {{PreviewWidth}}x{{PreviewHeight}} pixel frame that
        cannot scroll. Design for exactly that canvas:
        - Start the stylesheet with html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden}
        - Every element must fit inside {{PreviewWidth}}x{{PreviewHeight}}. Nothing may overflow
          in either direction, and no scrollbar may appear.
        - Scale type and spacing to suit it: small font sizes, tight padding, no large fixed
          widths or heights, and no min-width or min-height that exceeds the frame.
        - Prefer a single screen of content. Drop anything that will not fit rather than
          letting it spill.
        """;
}
