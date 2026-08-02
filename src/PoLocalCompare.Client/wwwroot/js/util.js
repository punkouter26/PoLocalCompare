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
 * Scrolls the named element into view if it exists. Used by Archive.razor to drop the user
 * at the duel-details panel after a row select — <c>JS.InvokeVoidAsync("eval", ...)</c> would
 * work, but it executes arbitrary page code on every detail-panel focus and is hostile to any
 * future Content-Security-Policy that disables inline-eval. This helper does the same job
 * without the cost.
 *
 * @param {string} id - Target element id.
 * @param {ScrollIntoViewOptions} [options] - Optional behaviour tuning (block, behavior, …).
 */
window.scrollElementIntoView = function (id, options) {
    const el = document.getElementById(id);
    if (el) el.scrollIntoView(options || { behavior: 'smooth', block: 'nearest' });
};
