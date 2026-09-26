// Utility helpers used by Blazor JS interop

/**
 * Escapes a string for interpolation into HTML markup.
 *
 * Everything that reaches the source viewer below is untrusted: the model output by definition,
 * and the model name too — it is Model.DisplayName, which any signed-in user can set through
 * POST /api/models with no character restrictions. The viewer builds a document that
 * window.open()s from a blob: URL, and a blob: URL inherits this app's origin, so an unescaped
 * name would run script with the victim's BFF session (the app's only CSP directive is
 * frame-ancestors, which does not restrict script). modelName went unescaped until 2026-09-02.
 *
 * @param {string} value
 * @returns {string}
 */
function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

/**
 * Opens the given HTML string in a new browser tab so the user can inspect the source.
 * @param {string} html - The raw HTML output from the model.
 * @param {string} modelName - Used as the page title and tab label.
 */
window.openHtmlSource = function (html, modelName) {
    const escaped = escapeHtml(html);
    const escapedName = escapeHtml(modelName);
    const page = `<!DOCTYPE html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Source — ${escapedName}</title>
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
    <span>${escapedName} • ${html.length.toLocaleString()} chars</span>
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
 * "Your duel/bracket finished" while the tab is in the background. ask() runs from the Compare
 * and Start clicks because browsers ignore a permission request without a user gesture; notify()
 * stays silent when the tab is visible, since the page itself is already showing the result.
 * ponytail: page-level Notification only — Android Chrome throws on the constructor (it requires
 * ServiceWorkerRegistration.showNotification), so mobile gets nothing; route through the service
 * worker if that matters.
 */
window.poNotify = {
    ask() {
        try {
            if ('Notification' in window && Notification.permission === 'default') Notification.requestPermission();
        } catch { /* decoration */ }
    },
    notify(title, body) {
        try {
            if (document.visibilityState !== 'hidden' || Notification.permission !== 'granted') return;
            const n = new Notification(title, { body, icon: '/favicon.png' });
            n.onclick = () => { window.focus(); n.close(); };
        } catch { /* decoration */ }
    }
};

/**
 * The GPU lease: the single answer to "may anything draw right now?".
 *
 * Browser models run WebLLM over WebGPU in this tab, and the tok/s the race reports is measured
 * while that happens. Any render loop that competes for the GPU makes that number wrong, so
 * every canvas effect in the app (fx.js's confetti burst) asks this first and does nothing
 * while it is held.
 *
 * webllm-interop.js acquires it when a worker starts and releases it when that worker completes,
 * errors or is terminated, so the Arena and the Tournament page (the two places a browser model
 * runs) are covered by the same hook and neither has to remember to pause anything. Holders are
 * counted, not flagged: two browser models in one duel hold two leases, and the GPU is free only
 * when both have finished.
 *
 * Classic script rather than a module so it exists before the first worker is created;
 * webllm-interop.js is a classic script too and cannot import one synchronously.
 */
window.poGpuLease = (() => {
    const holders = new Set();
    const listeners = new Set();

    const publish = () => {
        const busy = holders.size > 0;
        // Mirrored onto <html> so CSS can switch GPU-heavy treatments off with no JS per frame.
        try { document.documentElement.dataset.gpu = busy ? 'busy' : 'free'; } catch { /* pre-DOM */ }
        for (const fn of listeners) {
            try { fn(busy); } catch { /* a broken listener must not block the others */ }
        }
    };

    return {
        acquire(owner) { holders.add(String(owner)); publish(); },
        release(owner) { if (holders.delete(String(owner))) publish(); },
        busy() { return holders.size > 0; },
        /** Calls fn(busy) on every change. Returns an unsubscribe function. */
        subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    };
})();

// Light dismiss for the nav's native <details> user menu: a click anywhere outside it closes
// it. Without this the panel only closed on Escape, navigation or the trigger, and otherwise
// floated over the page after a theme or sound toggle.
document.addEventListener('click', (e) => {
    for (const menu of document.querySelectorAll('details.navmenu__menu[open]')) {
        if (!menu.contains(e.target)) menu.removeAttribute('open');
    }
});
