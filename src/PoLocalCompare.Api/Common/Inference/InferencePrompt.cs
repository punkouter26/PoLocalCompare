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
///
/// <para>
/// <b>This string is now <c>const</c> on purpose.</b> Azure AI Foundry (and every other
/// vendor that exposes an OpenAI-compatible chat endpoint) keys prompt-cache hits on prefix
/// equality — a chat call whose first N tokens match a cached call gets to skip the prefill on
/// them. The system prompt is the prefix that varies the least in this app (it is byte-equal
/// across every duel side), so a stable system prompt is the foundation of the cache working
/// at all. Keeping the whole string in one place and <c>const</c>-folding it guarantees the
/// compiler cannot emit a divergent copy under any code path. Edit with care: a whitespace
/// change here invalidates the cache for every cached call.
/// </para>
/// </remarks>
public static class InferencePrompt
{
    /// <summary>Width of the Arena preview frame, in CSS pixels.</summary>
    public const int PreviewWidth = 320;

    /// <summary>Height of the Arena preview frame, in CSS pixels.</summary>
    public const int PreviewHeight = 180;

    /// <summary>
    /// Compile-time constant so the JIT can fold it into the cached body and the string
    /// reference is byte-identical across every call. Changing this string requires bumping
    /// the <c>?v=</c> cache-buster on the worker copy in <c>webllm-worker.js</c> as well.
    /// </summary>
    public const string System = """
        You are an expert HTML/CSS coder. Return only valid HTML5 with inline CSS.
        No markdown, no explanation, no code fences.

        The page is displayed in a fixed 320x180 pixel frame that cannot scroll. Design for
        exactly that canvas:
        - Start the stylesheet with html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden}
        - Every element must fit inside 320x180. Nothing may overflow in either direction,
          and no scrollbar may appear.
        - Scale type and spacing to suit it: small font sizes, tight padding, no large fixed
          widths or heights, and no min-width or min-height that exceeds the frame.
        - Prefer a single screen of content. Drop anything that will not fit rather than
          letting it spill.
        """;
}
