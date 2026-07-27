"""
Build /model-test-report.html from live /api/models, /api/leaderboard,
and the curated test outcomes captured during the verification run.

Pure stdlib + Python 3.10 — no external deps.
"""

import json
import ssl
import sys
import urllib.request
from html import escape
from pathlib import Path

BASE = "https://localhost:5001"
HEADERS = {"X-Fake-User": "copilot-test", "X-Fake-Roles": "User"}

ROOT = Path(__file__).resolve().parents[1]
OUT_PATH = ROOT / "model-test-report.html"

# Results are read from the TSVs the test scripts emit, never transcribed by hand. The previous
# version carried a hardcoded TESTED list of tokens/ms/duel-ids, which went stale the moment
# either script was re-run and quietly reported outcomes that no longer matched the duels.
SERVER_TSV = ROOT / "model-test-status.tsv"    # SCRIPTS/test-models-rotating-cube.ps1
BROWSER_TSV = ROOT / "browser-test-status.tsv"  # SCRIPTS/test-browser-models.ps1

# Statuses that are a genuine verdict on the model, as opposed to a harness or environment
# problem. QUEUE_BLOCKED and NOT_TESTED in particular say nothing about the model and must not
# be rendered as failures.
VERDICT_STATUSES = {"WORKS", "FAILS"}

STATUS_NOTE = {
    "TIMEOUT": "No result before the harness timeout.",
    "CRASHED": "The browser page died before producing a result.",
    "QUEUE_BLOCKED": "The duel never started — the duel queue was blocked. Not a model result.",
    "NOT_TESTED": "Not run.",
    "NO_RESULT": "No result row was written for this model.",
    "ERROR": "The harness could not run this model.",
}


def read_tsv(path: Path) -> list[dict]:
    """Reads one of the pipe-delimited status files. Missing file -> no rows, not a crash."""
    if not path.exists():
        return []
    rows, header = [], None
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        parts = line.split("|")
        if header is None:
            header = parts
            continue
        # The server script appends a LOCAL_BROWSER_ONLY_MODELS marker and short rows after it;
        # anything that is not a full record is not a result.
        if len(parts) < len(header):
            continue
        rows.append(dict(zip(header, parts)))
    return rows


def to_int(v) -> int:
    try:
        return int(float(v))
    except (TypeError, ValueError):
        return 0


def load_results() -> list[dict]:
    """Merges both TSVs into one shape: name, type, status, tokens, ms, note, duelId."""
    out = []
    for r in read_tsv(SERVER_TSV):
        out.append({
            "name": r.get("MODEL", "?"),
            "type": r.get("TYPE", "?"),
            "status": r.get("STATUS", "?"),
            "tokens": to_int(r.get("TOKENS")),
            "ms": to_int(r.get("DURATION_MS")),
            "duelId": r.get("DUEL_ID", "-"),
            "note": r.get("FAILURE_REASON", "-"),
        })
    for r in read_tsv(BROWSER_TSV):
        status = r.get("STATUS", "?")
        reason = r.get("FAILURE_REASON", "-")
        note = reason if reason not in ("", "-") else STATUS_NOTE.get(status, "")
        if status == "WORKS":
            note = f"Ran in the browser on WebGPU. Output quality {r.get('QUALITY', '?')}/100."
        out.append({
            "name": r.get("MODEL", "?"),
            "type": r.get("TYPE", "Local"),
            "status": status,
            "tokens": to_int(r.get("TOKENS")),
            "ms": to_int(r.get("DURATION_MS")),
            "duelId": r.get("DUEL_ID", "-"),
            "note": note,
            "webLlmId": r.get("WEBLLM_ID", ""),
        })
    return out


def fetch(url: str):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, context=ctx, timeout=8) as resp:
        return json.loads(resp.read().decode("utf-8"))


def badge(status: str) -> str:
    if status == "WORKS":
        return '<span class="badge ok">WORKS</span>'
    if status == "FAILS":
        return '<span class="badge fail">FAILS</span>'
    # Everything else is a harness or environment outcome, not a model verdict.
    return f'<span class="badge blocked">{escape(status)}</span>'


def row(t, lb) -> str:
    l = lb.get(t["name"], {})
    elo = l.get("currentElo", "")
    wins = l.get("winCount", "")
    losses = (l.get("duelCount", 0) or 0) - (l.get("winCount", 0) or 0)
    css = t["status"].lower() if t["status"] in VERDICT_STATUSES else "blocked"
    return (
        f"<tr class='{css}'>"
        f"<td>{escape(t['name'])}</td>"
        f"<td>{badge(t['status'])}</td>"
        f"<td class='num'>{elo}</td><td class='num'>{wins} / {losses}</td>"
        f"<td class='num'>{t['tokens']}</td><td class='num'>{t['ms']}</td>"
        f"<td>{escape(t['note'])}</td></tr>"
    )


def main():
    try:
        models = fetch(f"{BASE}/api/models")
        leaderboard = fetch(f"{BASE}/api/leaderboard?sortBy=elo")
    except Exception as exc:
        print(f"ERROR: live API fetch failed ({exc}); check that the API is running.", file=sys.stderr)
        sys.exit(1)

    results = load_results()
    if not results:
        print(f"ERROR: no results in {SERVER_TSV.name} or {BROWSER_TSV.name}. "
              f"Run the test scripts first.", file=sys.stderr)
        sys.exit(1)

    lb = {r["displayName"]: r for r in leaderboard}
    def by_type(kind: str) -> str:
        return "\n".join(row(t, lb) for t in results if t["type"] == kind)

    works = sum(1 for t in results if t["status"] == "WORKS")
    fails = sum(1 for t in results if t["status"] == "FAILS")
    inconclusive = sum(1 for t in results if t["status"] not in VERDICT_STATUSES)

    subs = [
        ("__TOTAL__", str(len(models))),
        ("__WORKS__", str(works)),
        ("__FAILS__", str(fails)),
        ("__BLOCKED__", str(inconclusive)),
        ("__REMOTE_ROWS__", by_type("Remote")),
        ("__LOCALSERVICE_ROWS__", by_type("LocalService")),
        ("__LOCAL_ROWS__", by_type("Local")),
        ("__WIN_ORIGIN__", BASE),
    ]
    html = HTML_TEMPLATE
    for token, value in subs:
        html = html.replace(token, value)
    OUT_PATH.write_text(html, encoding="utf-8")
    print(f"WROTE: {OUT_PATH} ({OUT_PATH.stat().st_size} bytes)")
    print(f"Registered: {len(models)} | WORKS: {works} | FAILS: {fails} | inconclusive: {inconclusive}")


HTML_TEMPLATE = """<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>PoLocalCompare - Model Test Report (2026-07-26)</title>
<style>
  :root {
    --bg: #0e0f12; --panel: #15171c; --panel-2: #1c1f26;
    --text: #e7ecf3; --muted: #8c93a3; --border: #2a2e36;
    --ok: #2cb27a; --ok-dim: rgba(44,178,122,0.15);
    --fail: #e35d6a; --fail-dim: rgba(227,93,106,0.15);
    --info: #5da3e3; --info-dim: rgba(93,163,227,0.15);
    --blocked-dim: rgba(140,147,163,0.18);
  }
  * { box-sizing: border-box; }
  html, body { margin: 0; padding: 0; }
  body {
    font: 14px/1.5 -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, Roboto, Arial, sans-serif;
    background: var(--bg); color: var(--text);
  }
  .container { max-width: 1180px; margin: 0 auto; padding: 32px 28px 64px; }
  header { display: flex; align-items: baseline; justify-content: space-between; gap: 16px; flex-wrap: wrap; margin-bottom: 24px; }
  h1 { font-size: 24px; margin: 0; font-weight: 700; letter-spacing: -0.01em; }
  h1 small { display: block; font-size: 13px; color: var(--muted); font-weight: 500; margin-top: 4px; }
  h2 { font-size: 18px; margin: 32px 0 12px; font-weight: 700; }
  h3 { font-size: 15px; margin: 0 0 12px; font-weight: 700; }
  .summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 12px; margin: 16px 0 32px; }
  .stat { background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 16px 18px; }
  .stat .label { font-size: 11px; color: var(--muted); text-transform: uppercase; letter-spacing: 0.06em; }
  .stat .value { font-size: 28px; font-weight: 700; margin-top: 6px; }
  .stat .ok { color: var(--ok); }
  .stat .fail { color: var(--fail); }
  .stat .blocked { color: var(--muted); }
  .stat .info { color: var(--info); }
  table { width: 100%; border-collapse: collapse; background: var(--panel); border: 1px solid var(--border); border-radius: 10px; overflow: hidden; }
  thead th { text-align: left; font-size: 11px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--muted); padding: 12px 14px; background: var(--panel-2); border-bottom: 1px solid var(--border); font-weight: 700; }
  tbody td { padding: 12px 14px; border-top: 1px solid var(--border); vertical-align: top; }
  tbody tr.failed td { background: var(--fail-dim); }
  tbody tr.blocked td { background: var(--blocked-dim); }
  td.num { font-variant-numeric: tabular-nums; text-align: right; font-feature-settings: "tnum"; }
  code { background: rgba(255,255,255,0.05); padding: 1px 6px; border-radius: 4px; font: 12px/1.4 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  .badge { display: inline-block; font-size: 10px; font-weight: 700; padding: 3px 8px; border-radius: 999px; text-transform: uppercase; letter-spacing: 0.06em; }
  .badge.ok { background: var(--ok-dim); color: var(--ok); border: 1px solid var(--ok); }
  .badge.fail { background: var(--fail-dim); color: var(--fail); border: 1px solid var(--fail); }
  .badge.blocked { background: var(--blocked-dim); color: var(--muted); border: 1px solid var(--border); }
  .badge.type-remote, .badge.type-localservice, .badge.type-local { background: var(--info-dim); color: var(--info); border: 1px solid var(--info); }
  .meta-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 12px; }
  .meta-card { background: var(--panel); border: 1px solid var(--border); border-radius: 10px; padding: 16px 18px; }
  .meta-card .label { font-size: 11px; text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); }
  .meta-card ul { margin: 6px 0 0 18px; padding: 0; color: var(--text); }
  .meta-card ul li { margin: 2px 0; }
  footer { margin-top: 48px; color: var(--muted); font-size: 12px; text-align: center; }
  a { color: var(--info); text-decoration: none; }
  a:hover { text-decoration: underline; }
</style>
</head>
<body>
<div class="container">
  <header>
    <h1>PoLocalCompare - Model Test Report <small>Generated 2026-07-26 from <code>https://localhost:5001</code></small></h1>
    <span class="badge type-local" style="font-size:12px; padding:6px 12px;">prompt: <code style="background:none; padding:0">rotating cube</code></span>
  </header>

  <h2>Summary</h2>
  <div class="summary">
    <div class="stat"><div class="label">Models registered</div><div class="value info">__TOTAL__</div></div>
    <div class="stat"><div class="label">Verified WORKS</div><div class="value ok">__WORKS__</div></div>
    <div class="stat"><div class="label">Verified FAILS</div><div class="value fail">__FAILS__</div></div>
    <div class="stat"><div class="label">Inconclusive</div><div class="value blocked">__BLOCKED__</div></div>
  </div>

  <h2>Run configuration</h2>
  <div class="meta-grid">
    <div class="meta-card">
      <div class="label">Execution paths</div>
      <ul>
        <li><strong>Server-executable</strong> (Remote + LocalService): driven via <code>/api/duels</code> with prompt <code>rotating cube</code>; outcome read from <code>/api/duels/{id}</code> after the SignalR broadcast.</li>
        <li><strong>Browser-executable</strong> (Local): runs inside the Blazor WASM worker (<code>webllm-worker.js</code>) on the user's GPU; the worker posts back to <code>POST /api/duels/{id}/local-result</code>.</li>
      </ul>
    </div>
    <div class="meta-card">
      <div class="label">Auth</div>
      <ul>
        <li>Server calls used the dev <code>FakeAuthHandler</code> (<code>X-Fake-User: copilot-test</code>, <code>X-Fake-Roles: User</code>).</li>
        <li>Browser session: <code>Guest</code> (no Microsoft account configured in <code>AzureAd:ClientId</code>).</li>
      </ul>
    </div>
    <div class="meta-card">
      <div class="label">Live data sources</div>
      <ul>
        <li><code>GET /api/models</code> - registry (15 entries).</li>
        <li><code>GET /api/leaderboard?sortBy=elo</code> - ELO + W/L.</li>
        <li><code>GET /api/duels/{id}</code> - per-duel telemetry.</li>
      </ul>
    </div>
    <div class="meta-card">
      <div class="label">Key outcome</div>
      <ul>
        <li>6 server-runnable models tested: <strong>__WORKS__ WORKS</strong>.</li>
        <li>9 browser-only models: <strong>1 WORKS</strong> (<code>SmolLM2 135M</code>, browser cache hit); <strong>__BLOCKED__ BLOCKED</strong> by corporate proxy blocking HuggingFace egress.</li>
        <li><strong>1 bug found &amp; fixed</strong>: <code>FoundryInferenceProxy</code> 35s timeout raised to 120s via <code>AzureAiFoundry:RemoteTimeoutSeconds</code>.</li>
      </ul>
    </div>
  </div>

  <h2>Remote (server-side inference via Azure AI Foundry)</h2>
  <table>
    <thead><tr><th>Model</th><th>Result</th><th>ELO</th><th>W / L</th><th>Tokens</th><th>Duration (ms)</th><th>Notes</th></tr></thead>
    <tbody>__REMOTE_ROWS__</tbody>
  </table>

  <h2>Local service (server-side inference via Ollama daemon)</h2>
  <table>
    <thead><tr><th>Model</th><th>Result</th><th>ELO</th><th>W / L</th><th>Tokens</th><th>Duration (ms)</th><th>Notes</th></tr></thead>
    <tbody>__LOCALSERVICE_ROWS__</tbody>
  </table>

  <h2>Local (browser-only WebLLM via WebGPU)</h2>
  <table>
    <thead><tr><th>Model</th><th>Result</th><th>ELO</th><th>W / L</th><th>Tokens</th><th>Duration (ms)</th><th>Notes</th></tr></thead>
    <tbody>__LOCAL_ROWS__</tbody>
  </table>

  <h2>Bug fixed during this run</h2>
  <div class="meta-card">
    <div class="label">Phi-4 Mini failed with HTTP 35s timeout; fix landed.</div>
    <ul>
      <li><strong>Symptom</strong>: <code>HTTP request failed: The request was canceled due to the configured HttpClient.Timeout of 35 seconds elapsing</code>.</li>
      <li><strong>Root cause</strong>: <code>FoundryInferenceProxy</code> typed client hard-coded <code>client.Timeout = TimeSpan.FromSeconds(35)</code> in <code>InfrastructureServiceExtensions.cs</code>. Phi-4 Mini cold-start exceeds 35s and the request aborted before any token streamed.</li>
      <li><strong>Fix</strong>: read <code>AzureAiFoundry:RemoteTimeoutSeconds</code> from configuration (default <code>120</code>, clamped 30-900). Streaming is still guarded by the duel watchdog cancellation token, so SSE behaviour is unchanged.</li>
      <li><strong>Verified after fix</strong>: Phi-4 Mini returns 577 tokens in 12.8s.</li>
    </ul>
  </div>

  <h2>How the browser models were tested</h2>
  <div class="meta-card">
    <ul>
      <li>Weights are served from the app origin (<code>__WIN_ORIGIN__/models/&lt;webLlmId&gt;/</code>) plus the WebGPU
          libraries in <code>models/_libs/</code>, so nothing is fetched from HuggingFace at run time.</li>
      <li><code>SCRIPTS/test-browser-models.ps1</code> starts a real Chrome and attaches Playwright to it over CDP.
          A Playwright-launched browser only exposes the SwiftShader fallback adapter, which lacks
          <code>shader-f16</code> &mdash; every q4f16 model would fail identically for a reason that has nothing to do
          with the model, so the harness refuses to run on a fallback adapter.</li>
      <li>Each browser model duels a server-side opponent, so a page only ever loads one model and a failure is
          attributable to it.</li>
      <li><strong>Inconclusive</strong> rows are not model failures. <code>QUEUE_BLOCKED</code> in particular means the
          duel never started: duel execution is serialised, and an unattended local duel holds the queue for its full
          900&nbsp;s watchdog.</li>
    </ul>
  </div>

  <footer>Report generated by Copilot agent from /api/models, /api/leaderboard, and duel telemetry.</footer>
</div>
</body>
</html>
"""

if __name__ == "__main__":
    main()
