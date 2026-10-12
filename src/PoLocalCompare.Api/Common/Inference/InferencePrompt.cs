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
/// The behaviour half was added on 2026-10-11. The brief said "HTML/CSS coder… inline CSS"
/// and never mentioned script, while most of the prompt library asks for motion (a spinning
/// cube, Game of Life, a sorting race) and the judge marks a page that does not move as not
/// having done what was asked. It also states the two things the renderer enforces that a
/// model cannot guess: <c>HtmlScreenshotRenderer</c> blocks all network, so a CDN reference
/// is a dead reference, and it screenshots without clicking, so a page that waits for input
/// is judged on its idle frame. <b>Ratings from before this date were earned under the
/// shorter brief</b> and are not strictly comparable with ones earned after it.
/// </para>
///
/// <para>
/// <b>This string is <c>const</c> so there is exactly one copy</b> — the fairness argument above
/// is the reason, and <c>const</c>-folding guarantees no code path can emit a divergent one.
/// It is NOT a prompt-caching win, although an earlier version of this comment said so: Azure
/// OpenAI only caches prompts of 1,024 tokens or more, and this one is about 250 tokens plus a
/// short user prompt, so no duel call is ever cache-eligible. Don't pad it to cross the line —
/// the extra prefill would cost more than a cache hit on it could save.
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

        Behaviour belongs in the page too:
        - Use an inline <script> for anything the request needs to move, update or respond.
        - No external files of any kind (scripts, stylesheets, fonts, images, CDNs). The page
          runs with no network, so anything it has to fetch will be missing.
        - Anything that moves must start by itself when the page loads; do not wait for a
          click or a key press to begin.
        """;
}
