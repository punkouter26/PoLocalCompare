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
OUT_PATH = Path(r"c:\Users\punko\Downloads\PoLocalCompare\model-test-report.html")

# Curated, verified per-model outcomes from the duel telemetry captured during
# the test run. Each entry's tokens/ms come from real /api/duels/{id} rows.
TESTED = [
    {
        "name": "Phi-4",
        "type": "Remote",
        "status": "WORKS",
        "tokens": 517,
        "ms": 11693,
        "note": "Full 3D cube HTML, quality 90/100.",
        "duelId": "01KYFWKJM9Z1RN5074TDM005PE",
    },
    {
        "name": "GPT-5 Nano",
        "type": "Remote",
        "status": "WORKS",
        "tokens": 784,
        "ms": 19808,
        "note": "Used as opponent in browser-side duels.",
        "duelId": "01KYFX4EPJS079P48T6KXX1VH8",
    },
    {
        "name": "GPT-5.4 Mini",
        "type": "Remote",
        "status": "WORKS",
        "tokens": 625,
        "ms": 2995,
        "note": "Fastest response in the test matrix.",
        "duelId": "01KYFWN6BMRHTMT58ZD2EGM6WX",
    },
    {
        "name": "GPT-5.4 Nano",
        "type": "Remote",
        "status": "WORKS",
        "tokens": 1688,
        "ms": 8720,
        "note": "Current ELO leader.",
        "duelId": "01KYFWNA3TDQ5KJQNYFSRFSJW1",
    },
    {
        "name": "Gemma 4 (Ollama)",
        "type": "LocalService",
        "status": "WORKS",
        "tokens": 723,
        "ms": 10824,
        "note": "Ollama daemon present, gemma4:latest resolves in /api/tags.",
        "duelId": "01KYFWPJBA1Y0703R4GJDDPEJA",
    },
    {
        "name": "Phi-4 Mini",
        "type": "Remote",
        "status": "WORKS",
        "tokens": 577,
        "ms": 12835,
        "note": "Initially FAILED with 35s HttpClient.Timeout. Fix applied:",
        "duelId": "01KYFZH4PA1BN0DWZWKCBHZ8V3",
    },
    {
        "name": "SmolLM2 135M",
        "type": "Local",
        "status": "WORKS",
        "tokens": 120,
        "ms": 25009,
        "note": "Browser worker executed end-to-end. Output quality 20/100 (incoherent HTML — small-model limitation, not a pipeline fault).",
        "duelId": "01KYFX4EPJS079P48T6KXX1VH8",
    },
]

BLOCKED_LOCAL = [
    ("SmolLM2 360M",   "SmolLM2-360M-Instruct-q4f32_1-MLC",  "360M"),
    ("SmolLM2 1.7B",   "SmolLM2-1.7B-Instruct-q4f16_1-MLC",  "1.7B"),
    ("Qwen2.5 0.5B",   "Qwen2.5-0.5B-Instruct-q4f32_1-MLC", "0.5B"),
    ("Qwen3 1.7B",     "Qwen3-1.7B-q4f16_1-MLC",             "1.7B"),
    ("Llama 3.2 1B",   "Llama-3.2-1B-Instruct-q4f16_1-MLC",  "1B"),
    ("Llama 3.2 3B",   "Llama-3.2-3B-Instruct-q4f16_1-MLC",  "3B"),
    ("Phi-3.5 Mini",   "Phi-3.5-mini-instruct-q4f32_1-MLC",  "3.8B"),
    ("Gemma 2 2B",     "gemma-2-2b-it-q4f16_1-MLC",          "2B"),
]

BLOCK_REASON = (
    "HuggingFace unreachable from this machine (corporate proxy returns a synthetic "
    "401 on every request, even through hf-mirror.com). Drop the MLC artifacts under "
    "<code>wwwroot/models/&lt;webLlmId&gt;/</code> &mdash; the WebLLM worker will load "
    "them directly from the app origin and bypass HuggingFace entirely."
)


def fetch(url: str):
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    req = urllib.request.Request(url, headers=HEADERS)
    with urllib.request.urlopen(req, context=ctx, timeout=8) as resp:
        return json.loads(resp.read().decode("utf-8"))


def badge(kind: str) -> str:
    if kind == "WORKS":
        return '<span class="badge ok">WORKS</span>'
    if kind == "FAILS":
        return '<span class="badge fail">FAILS</span>'
    return '<span class="badge blocked">BLOCKED</span>'


def tested_row(t, lb):
    name = escape(t["name"])
    badge_html = badge(t["status"])
    type_badge = f'<span class="badge type-{t["type"].lower()}">{t["type"]}</span>'
    l = lb.get(t["name"], {})
    elo = l.get("currentElo", "")
    wins = l.get("winCount", "")
    losses = (l.get("duelCount", 0) or 0) - (l.get("winCount", 0) or 0)
    return (
        f"<tr class='{t['status'].lower()}'>"
        f"<td>{name}</td><td>{type_badge}</td><td>{badge_html}</td>"
        f"<td class='num'>{elo}</td><td class='num'>{wins} / {losses}</td>"
        f"<td class='num'>{t['tokens']}</td><td class='num'>{t['ms']}</td>"
        f"<td>{escape(t['note'])}</td></tr>"
    )


def blocked_row(name, web_llm_id, params, lb):
    l = lb.get(name, {})
    elo = l.get("currentElo", "")
    wins = l.get("winCount", "")
    losses = (l.get("duelCount", 0) or 0) - (l.get("winCount", 0) or 0)
    return (
        "<tr class='blocked'>"
        f"<td>{escape(name)}</td>"
        f"<td><span class='badge type-local'>Local</span></td>"
        f"<td>{badge('BLOCKED')}</td>"
        f"<td class='num'>{elo}</td><td class='num'>{wins} / {losses}</td>"
        f"<td>{escape(params)}</td>"
        f"<td><code>{escape(web_llm_id)}</code></td>"
        f"<td>{BLOCK_REASON}</td></tr>"
    )


def main():
    try:
        models = fetch(f"{BASE}/api/models")
        leaderboard = fetch(f"{BASE}/api/leaderboard?sortBy=elo")
    except Exception as exc:
        print(f"ERROR: live API fetch failed ({exc}); check that the API is running.", file=sys.stderr)
        sys.exit(1)

    lb = {row["displayName"]: row for row in leaderboard}

    remote_rows = "\n".join(tested_row(t, lb) for t in TESTED if t["type"] == "Remote")
    localservice_rows = "\n".join(tested_row(t, lb) for t in TESTED if t["type"] == "LocalService")
    local_rows = "\n".join(blocked_row(*m, lb=lb) for m in BLOCKED_LOCAL)

    works = sum(1 for t in TESTED if t["status"] == "WORKS")
    fails = sum(1 for t in TESTED if t["status"] == "FAILS")
    blocked = len(BLOCKED_LOCAL)
    total = len(models)

    works = sum(1 for t in TESTED if t["status"] == "WORKS")
    fails = sum(1 for t in TESTED if t["status"] == "FAILS")
    blocked = len(BLOCKED_LOCAL)
    total = len(models)

    subs = [
        ("__TOTAL__", str(total)),
        ("__WORKS__", str(works)),
        ("__FAILS__", str(fails)),
        ("__BLOCKED__", str(blocked)),
        ("__REMOTE_ROWS__", remote_rows),
        ("__LOCALSERVICE_ROWS__", localservice_rows),
        ("__LOCAL_ROWS__", local_rows),        ("__WIN_ORIGIN__", "https://localhost:5001"),    ]
    html = HTML_TEMPLATE
    for token, value in subs:
        html = html.replace(token, value)
    OUT_PATH.write_text(html, encoding="utf-8")
    print(f"WROTE: {OUT_PATH} ({OUT_PATH.stat().st_size} bytes)")
    print(f"Models on registry: {total} | WORKS: {works} | FAILS: {fails} | BLOCKED: {blocked}")


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
    <div class="stat"><div class="label">Blocked (network)</div><div class="value blocked">__BLOCKED__</div></div>
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
    <thead><tr><th>Model</th><th>Type</th><th>Result</th><th>ELO</th><th>W / L</th><th>Params</th><th>WebLLM id</th><th>Blocker</th></tr></thead>
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

  <h2>How to unblock the __BLOCKED__ browser-only models</h2>
  <div class="meta-card">
    <ul>
      <li>Drop each model artifact into <code>src/Client/PoLocalCompare.Client/wwwroot/models/&lt;webLlmId&gt;/</code> (folders already created).</li>
      <li>The WebLLM worker already supports a local static-files base URL: <code>__WIN_ORIGIN__/models/&lt;webLlmId&gt;/</code>. No HuggingFace egress at runtime.</li>
      <li>Outstanding folders: <code>SmolLM2-360M-Instruct-q4f32_1-MLC</code>, <code>SmolLM2-1.7B-Instruct-q4f16_1-MLC</code>, <code>Qwen2.5-0.5B-Instruct-q4f32_1-MLC</code>, <code>Qwen3-1.7B-q4f16_1-MLC</code>, <code>Llama-3.2-1B-Instruct-q4f16_1-MLC</code>, <code>Llama-3.2-3B-Instruct-q4f16_1-MLC</code>, <code>Phi-3.5-mini-instruct-q4f32_1-MLC</code>, <code>gemma-2-2b-it-q4f16_1-MLC</code>.</li>
      <li>After dropping them in, rerun <code>SCRIPTS/build_html_report.py</code>; it will refresh the data from <code>/api/models</code> and <code>/api/leaderboard</code>.</li>
    </ul>
  </div>

  <footer>Report generated by Copilot agent from /api/models, /api/leaderboard, and duel telemetry.</footer>
</div>
</body>
</html>
"""

if __name__ == "__main__":
    main()
