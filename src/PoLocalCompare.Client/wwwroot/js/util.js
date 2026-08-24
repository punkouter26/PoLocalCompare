// Utility helpers used by Blazor JS interop

/**
 * Opens the given HTML string in a new browser tab so the user can inspect the source.
 * @param {string} html - The raw HTML output from the model.
 * @param {string} modelName - Used as the page title and tab label.
 */
window.openHtmlSource = function (html, modelName) {
    const escaped = html
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;');
    const page = `<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Source — ${modelName}</title>
  <style>
    body { background: #0d1117; color: #e6edf3; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 13px; margin: 0; }
    header { background: #161b22; padding: 10px 18px; border-bottom: 1px solid #30363d; display:flex; align-items:center; gap:12px; position:sticky; top:0; }
    header h1 { margin:0; font-size:14px; color:#58a6ff; }
    header span { color:#8b949e; font-size:12px; }
    pre { margin: 0; padding: 18px; white-space: pre-wrap; word-break: break-all; line-height:1.6; }
  </style>
</head>
<body>
  <header>
    <h1>&#60;/&#62; HTML Source</h1>
    <span>${modelName} • ${html.length.toLocaleString()} chars</span>
  </header>
  <pre>${escaped}</pre>
</body>
</html>`;
    const blob = new Blob([page], { type: 'text/html' });
    const url = URL.createObjectURL(blob);
    const tab = window.open(url, '_blank');
    // Revoke after the tab has had time to read the blob
    if (tab) setTimeout(() => URL.revokeObjectURL(url), 60000);
};


/**
 * Moves keyboard focus to an element by id.
 *
 * Needed for the skip link: Blazor intercepts same-document anchor clicks and handles them as
 * router navigation, so the browser's native "scroll to the fragment target and focus it"
 * behaviour never runs. Without this the link scrolls but leaves focus in the nav, which
 * defeats the entire point of SC 2.4.1 Bypass Blocks for a keyboard user.
 *
 * @param {string} id - Target element id. The element needs tabindex="-1" to accept focus.
 */
window.focusElement = function (id) {
    const el = document.getElementById(id);
    if (el) {
        el.focus();
        el.scrollIntoView({ block: 'start' });
    }
};

/**
 * Returns the browser's current `window.location.href` as a string. Used by Home.razor when
 * the URL has query parameters (slot ids) that Blazor's named-supply-parameter matcher
 * occasionally drops — having the raw value as a fallback lets the page read the slots
 * regardless of how the framework bound the parameters.
 */
window.getLocationHref = function () {
    return window.location.href;
};

/**
 * Command palette hotkey — Ctrl/⌘-K, registered once from CommandPalette.OnInitializedAsync.
 *
 * A single delegated listener on the document rather than per-component key handling: the
 * palette has to open from any page and from inside any input, and Blazor's @onkeydown only
 * fires for elements it rendered.
 *
 * Deliberately does NOT fire while the user is typing in a textarea — the prompt box is the
 * main input on this app and swallowing a keystroke there would be worse than the shortcut
 * being unavailable in that one place.
 */
let paletteRef = null;
let paletteHandler = null;

window.registerPaletteHotkey = (dotNetRef) => {
    paletteRef = dotNetRef;
    if (paletteHandler) return;

    paletteHandler = (event) => {
        if (!paletteRef) return;

        const isToggle = (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k';
        if (!isToggle) return;

        const tag = document.activeElement && document.activeElement.tagName;
        if (tag === 'TEXTAREA') return;

        event.preventDefault();
        paletteRef.invokeMethodAsync('ToggleAsync').catch(() => {
            // Component disposed between keypress and dispatch — drop it.
        });
    };

    document.addEventListener('keydown', paletteHandler);
};

window.unregisterPaletteHotkey = () => {
    if (paletteHandler) {
        document.removeEventListener('keydown', paletteHandler);
        paletteHandler = null;
    }
    paletteRef = null;
};

/**
 * Best-effort haptic tap. Desktop browsers and iOS Safari have no Vibration API, and a browser
 * may refuse without a prior user gesture — a verdict click is a gesture, so this fires there.
 *
 * Moved here from compare.js on 2026-08-23, when that file's other five helpers (the sandbox
 * runtime probe, synced scroll panes, the clipboard copy and the share-card canvas renderer)
 * lost their callers along with the Objective scorecard, the Code/Diff views and the duel
 * export. One function did not justify its own script tag.
 */
window.hapticPulse = function (pattern) {
    try {
        if (navigator.vibrate) navigator.vibrate(pattern);
    } catch {
        /* vibration is decoration — never let it surface as an error */
    }
};
