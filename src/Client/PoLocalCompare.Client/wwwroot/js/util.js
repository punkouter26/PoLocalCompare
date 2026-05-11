// Utility helpers used by Blazor JS interop

window.downloadFileFromBase64 = function (fileName, contentType, base64Data) {
    const byteChars = atob(base64Data);
    const byteNumbers = new Uint8Array(byteChars.length);
    for (let i = 0; i < byteChars.length; i++) {
        byteNumbers[i] = byteChars.charCodeAt(i);
    }
    const blob = new Blob([byteNumbers], { type: contentType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
};

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

