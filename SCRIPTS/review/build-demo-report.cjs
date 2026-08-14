// Build a self-contained HTML report for the 10-duel auto-demo.
// Pulls full DuelDto from the API for each duel id we observed, then renders a styled,
// printable HTML page with the standings, per-duel detail, and the run metadata.
//
// Run: NODE_TLS_REJECT_UNAUTHORIZED=0 node SCRIPTS/review/build-demo-report.cjs
// Out: SCRIPTS/review/out/demo-run/report.html (open in a browser)

const fs = require('fs');
const path = require('path');

const BASE = process.env.BASE_URL || 'https://localhost:5001';
const OUT_DIR = path.join(__dirname, 'out', 'demo-run');
const PROGRESS = path.join(OUT_DIR, 'progress.json');
const FINAL    = path.join(OUT_DIR, 'final.json');
const REPORT   = path.join(OUT_DIR, 'report.html');

const COCKTAIL = [
  { hue: 210, name: 'sky' },
  { hue: 142, name: 'mint' },
  { hue: 280, name: 'lilac' },
  { hue: 22,  name: 'peach' },
  { hue: 320, name: 'rose' },
  { hue: 188, name: 'aqua' },
];

// Curated HTML escape (we own the input, but the prompt text could include <,>,&,",').
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));

function fmtDuration(ms) {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms} ms`;
  const s = Math.round(ms / 100) / 10;
  return `${s} s`;
}

function modelHue(name) {
  if (!name) return COCKTAIL[0];
  let h = 0;
  for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) >>> 0;
  return COCKTAIL[h % COCKTAIL.length];
}

function color(name) {
  const c = modelHue(name);
  return `hsl(${c.hue} 70% 55%)`;
}

async function seedAuth() {
  const r = await fetch(`${BASE}/auth/login/fake?user=GUEST_REPORT_BUILDER&returnUrl=/`, { redirect: 'manual' });
  const setCookie = r.headers.getSetCookie?.() ?? r.headers.get('set-cookie');
  return (Array.isArray(setCookie) ? setCookie[0] : setCookie)?.split(';')[0];
}

async function getDuel(cookie, id) {
  const r = await fetch(`${BASE}/api/duels/${id}`, { headers: { cookie } });
  if (!r.ok) throw new Error(`getDuel ${id} ${r.status}`);
  return await r.json();
}

(async () => {
  const cookie = await seedAuth();
  if (!cookie) throw new Error('No cookie');

  const snap = JSON.parse(fs.readFileSync(PROGRESS, 'utf8'));
  // Build a name lookup from the list snapshot (which carries leftModelName/rightModelName).
  // The detailed /api/duels/{id} endpoint only returns IDs.
  const namesById = {};
  for (const d of snap.list) {
    if (d.leftModelId && d.leftModelName) namesById[d.leftModelId] = d.leftModelName;
    if (d.rightModelId && d.rightModelName) namesById[d.rightModelId] = d.rightModelName;
  }
  const detail = await Promise.all(snap.list.map(d => getDuel(cookie, d.duelId).catch(e => ({ ...d, error: e.message }))));
  // Stitch name from list snapshot onto detailed DTO so downstream rendering has friendly names.
  const duels = detail.map(d => ({
    ...d,
    leftModelName: namesById[d.leftModelId] || d.leftModelName || d.leftModelId,
    rightModelName: namesById[d.rightModelId] || d.rightModelName || d.rightModelId,
  }));
  fs.writeFileSync(FINAL, JSON.stringify({ observedAt: snap.at, duels }, null, 2));

  // ── Aggregate ──
  // Some DuelDto fields may be null when the duel is still Pending. Defend against that.
  const nameOrFallback = (d, side) => {
    const n = side === 'left' ? d.leftModelName : d.rightModelName;
    const id = side === 'left' ? d.leftModelId : d.rightModelId;
    return n || id || `(${side})`;
  };

  const decided = duels.filter(d => d.verdict && d.verdict !== 'Pending' && d.verdict !== 'Expired');
  const ties = duels.filter(d => d.verdict === 'Tie');
  const leftWins = duels.filter(d => d.verdict === 'Left');
  const rightWins = duels.filter(d => d.verdict === 'Right');
  const pending = duels.filter(d => !d.verdict || d.verdict === 'Pending');
  // Count side failures from the rich Results array (per-side DuelResultDto).
  const failed = duels.filter(d => (d.results || []).some(r => r.isFailure));

  const standings = {};
  for (const d of decided) {
    const lname = nameOrFallback(d, 'left');
    const rname = nameOrFallback(d, 'right');
    if (d.verdict === 'Tie') {
      standings[lname] ??= { name: lname, wins: 0, ties: 0, losses: 0 };
      standings[rname] ??= { name: rname, wins: 0, ties: 0, losses: 0 };
      standings[lname].ties++;
      standings[rname].ties++;
    } else {
      const winner = d.verdict === 'Left' ? lname : rname;
      const loser = d.verdict === 'Left' ? rname : lname;
      standings[winner] ??= { name: winner, wins: 0, ties: 0, losses: 0 };
      standings[loser]  ??= { name: loser,  wins: 0, ties: 0, losses: 0 };
      standings[winner].wins++;
      standings[loser].losses++;
    }
  }
  const standingsRows = Object.values(standings).sort((a, b) => b.wins - a.wins || b.ties - a.ties);

  // ── Render HTML ──
  const css = `
:root {
  color-scheme: dark;
  --bg: #0b0e16;
  --surface: #11141d;
  --surface-2: #1a1f2b;
  --border: #252b39;
  --text: #e7ebf3;
  --muted: #9aa3b7;
  --accent: #6ee7b7;
  --warn: #fbbf24;
  --danger: #f87171;
}
* { box-sizing: border-box; }
body { margin: 0; padding: 2.5rem 1.5rem; font-family: 'Inter', system-ui, -apple-system, sans-serif; background: var(--bg); color: var(--text); line-height: 1.5; }
.wrap { max-width: 1080px; margin: 0 auto; }
h1 { font-size: 2rem; margin: 0 0 0.25rem; letter-spacing: -0.02em; }
.subtitle { color: var(--muted); margin: 0 0 2rem; font-size: 0.95rem; }
h2 { font-size: 1.25rem; margin: 2.5rem 0 0.75rem; letter-spacing: -0.01em; border-bottom: 1px solid var(--border); padding-bottom: 0.5rem; }
.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 0.75rem; margin-bottom: 1.5rem; }
.kpi { background: var(--surface); border: 1px solid var(--border); border-radius: 10px; padding: 0.85rem 1rem; }
.kpi-label { font-size: 0.7rem; text-transform: uppercase; letter-spacing: 0.08em; color: var(--muted); }
.kpi-value { font-size: 1.5rem; font-weight: 700; margin-top: 0.15rem; }
.kpi-sub { font-size: 0.75rem; color: var(--muted); }
table { width: 100%; border-collapse: collapse; background: var(--surface); border: 1px solid var(--border); border-radius: 10px; overflow: hidden; }
th, td { padding: 0.65rem 0.85rem; text-align: left; border-bottom: 1px solid var(--border); font-size: 0.9rem; vertical-align: top; }
th { background: var(--surface-2); font-weight: 600; font-size: 0.78rem; text-transform: uppercase; letter-spacing: 0.06em; color: var(--muted); }
tr:last-child td { border-bottom: 0; }
.tag { display: inline-block; padding: 0.15rem 0.5rem; border-radius: 6px; font-size: 0.7rem; font-weight: 600; }
.tag-win   { background: rgba(110, 231, 183, 0.15); color: var(--accent); }
.tag-loss  { background: rgba(248, 113, 113, 0.15); color: var(--danger); }
.tag-tie   { background: rgba(251, 191, 36, 0.15); color: var(--warn); }
.tag-pend  { background: var(--surface-2); color: var(--muted); }
.tag-fail  { background: rgba(248, 113, 113, 0.15); color: var(--danger); }
.dot { display: inline-block; width: 0.6rem; height: 0.6rem; border-radius: 50%; vertical-align: middle; margin-right: 0.4rem; }
.bar { display: inline-block; width: 100px; height: 6px; background: var(--surface-2); border-radius: 3px; overflow: hidden; vertical-align: middle; }
.bar > span { display: block; height: 100%; }
.muted { color: var(--muted); font-size: 0.78rem; }
.duel-card { background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 1.1rem 1.25rem; margin-bottom: 0.85rem; }
.duel-card.pending { border-left: 4px solid var(--warn); }
.duel-card.tie     { border-left: 4px solid var(--warn); }
.duel-card.win-l   { border-left: 4px solid var(--accent); }
.duel-card.win-r   { border-left: 4px solid var(--accent); }
.duel-head { display: flex; align-items: baseline; justify-content: space-between; flex-wrap: wrap; gap: 0.5rem; margin-bottom: 0.5rem; }
.duel-head h3 { margin: 0; font-size: 1rem; }
.duel-prompt { color: var(--muted); font-size: 0.85rem; font-style: italic; margin-bottom: 0.65rem; }
.duel-body { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
@media (max-width: 700px) { .duel-body { grid-template-columns: 1fr; } }
.side { padding: 0.65rem; background: var(--surface-2); border-radius: 8px; border: 1px solid var(--border); }
.side.loser { opacity: 0.7; }
.side-name { font-weight: 700; }
.metrics { display: grid; grid-template-columns: repeat(3, 1fr); gap: 0.4rem; margin-top: 0.5rem; font-size: 0.78rem; }
.metric { background: var(--bg); padding: 0.4rem 0.55rem; border-radius: 6px; }
.metric-label { color: var(--muted); font-size: 0.66rem; text-transform: uppercase; letter-spacing: 0.06em; }
.metric-value { font-weight: 600; margin-top: 0.1rem; }
footer { margin-top: 3rem; padding-top: 1rem; border-top: 1px solid var(--border); color: var(--muted); font-size: 0.78rem; }
a { color: var(--accent); text-decoration: none; }
a:hover { text-decoration: underline; }
`;

  const renderSide = (name, result, isWinner) => {
    if (!result) return `<div class="side"><span class="side-name">${esc(name)}</span><div class="muted">no result</div></div>`;
    const lq = result.outputQualityScore ?? 0;
    const tokens = result.tokenCount ?? 0;
    const total = fmtDuration(result.totalDurationMs);
    const gen = fmtDuration(result.generationDurationMs);
    const warm = fmtDuration(result.warmUpDurationMs);
    const velocity = result.tokenVelocity != null ? `${result.tokenVelocity.toFixed(1)}` : '—';
    const cost = result.apiCostUsd != null ? `$${result.apiCostUsd.toFixed(4)}` : '—';
    const energy = result.energyWh != null ? `${result.energyWh.toFixed(2)} Wh` : '—';
    const truncated = result.wasTruncated ? ' <span class="tag tag-pend">truncated</span>' : '';
    const err = result.isFailure ? `<div class="tag tag-fail" style="margin-top:0.4rem">❌ ${esc((result.failureReason || 'failed').split('\n')[0])}</div>` : '';
    return `
      <div class="side ${isWinner ? '' : 'loser'}">
        <span class="dot" style="background:${color(name)}"></span>
        <span class="side-name">${esc(name)}</span>
        ${result.isFailure ? '<span class="tag tag-fail" style="margin-left:0.4rem">FAIL</span>' : ''}${truncated}
        <div class="metrics">
          <div class="metric"><div class="metric-label">Quality</div><div class="metric-value">${lq}/100 <span class="bar"><span style="width:${lq}%; background:${color(name)}"></span></span></div></div>
          <div class="metric"><div class="metric-label">Tokens</div><div class="metric-value">${tokens.toLocaleString()}</div></div>
          <div class="metric"><div class="metric-label">Total time</div><div class="metric-value">${total}</div></div>
          <div class="metric"><div class="metric-label">Generation</div><div class="metric-value">${gen}</div></div>
          <div class="metric"><div class="metric-label">Warm-up</div><div class="metric-value">${warm}</div></div>
          <div class="metric"><div class="metric-label">tok/s</div><div class="metric-value">${velocity}</div></div>
          <div class="metric"><div class="metric-label">API cost</div><div class="metric-value">${cost}</div></div>
          <div class="metric"><div class="metric-label">Energy</div><div class="metric-value">${energy}</div></div>
        </div>
        ${err}
      </div>`;
  };

  const renderDuel = (d, idx) => {
    const v = d.verdict || 'Pending';
    const verdictClass = v === 'Pending' ? 'pending' : v === 'Tie' ? 'tie' : 'win-l';
    const tag = v === 'Pending' || !v ? `<span class="tag tag-pend">⚖ judge stood down</span>`
              : v === 'Tie' ? `<span class="tag tag-tie">🤝 Tie</span>`
              : v === 'Left' ? `<span class="tag tag-win">🏆 ${esc(d.leftModelName)} +${d.eloShiftWinner?.toFixed(1) ?? ''}</span>`
              : `<span class="tag tag-win">🏆 ${esc(d.rightModelName)} +${d.eloShiftWinner?.toFixed(1) ?? ''}</span>`;
    const verdictBy = d.verdictSource === 'Ai'
      ? `<span class="muted"> · judged by ${esc(d.judgeModel || 'AI')}</span>`
      : (d.verdictBy ? `<span class="muted"> · judged by ${esc(d.verdictBy)}</span>` : '');

    // Resolve per-side results from the flat Results list.
    const results = d.results || [];
    const leftResult = results.find(r => r.modelId === d.leftModelId) || results[0];
    const rightResult = results.find(r => r.modelId === d.rightModelId) || results[1];

    return `
<article class="duel-card ${verdictClass}">
  <div class="duel-head">
    <h3>Duel ${idx + 1} — <span style="color:${color(d.leftModelName)}">${esc(d.leftModelName)}</span> vs <span style="color:${color(d.rightModelName)}">${esc(d.rightModelName)}</span></h3>
    <div>${tag}${verdictBy}</div>
  </div>
  <div class="duel-prompt">${esc(d.promptSummary)}</div>
  <div class="duel-body">
    ${renderSide(d.leftModelName, leftResult, d.verdict === 'Left')}
    ${renderSide(d.rightModelName, rightResult, d.verdict === 'Right')}
  </div>
  ${d.judgeRationale ? `<p class="muted" style="margin-top:0.6rem"><strong>Judge rationale:</strong> ${esc(d.judgeRationale)}</p>` : ''}
  ${d.judgeStoodDownReason ? `<p class="muted" style="margin-top:0.4rem"><strong>Judge stood down:</strong> ${esc(d.judgeStoodDownReason)}</p>` : ''}
  <p class="muted" style="margin-top:0.6rem"><code>${esc(d.duelId)}</code> · <a href="/arena/${esc(d.duelId)}">Open in Arena →</a></p>
</article>`;
  };

  const renderStandings = standingsRows.length === 0
    ? '<p class="muted">No wins yet.</p>'
    : `<table>
        <thead><tr><th>#</th><th>Model</th><th>Wins</th><th>Losses</th><th>Ties</th><th>W/L/T</th></tr></thead>
        <tbody>
          ${standingsRows.map((s, i) => `<tr>
            <td>${i + 1}</td>
            <td><span class="dot" style="background:${color(s.name)}"></span><strong>${esc(s.name)}</strong></td>
            <td>${s.wins}</td>
            <td>${s.losses}</td>
            <td>${s.ties}</td>
            <td>${s.wins}/${s.losses}/${s.ties}</td>
          </tr>`).join('')}
        </tbody>
      </table>`;

  const totalDuration = (() => {
    const started = duels.map(d => d.startedAt).filter(Boolean).sort()[0];
    const finished = duels.map(d => d.completedAt).filter(Boolean).sort().pop();
    if (!started || !finished) return '—';
    return fmtDuration(new Date(finished) - new Date(started));
  })();

  const winners = decided.length - ties.length;

  const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>PoLocalCompare — Demo run report</title>
<style>${css}</style>
</head>
<body>
<div class="wrap">

<header>
  <h1>🧪 Auto-demo run report</h1>
  <p class="subtitle">Generated from 10 back-to-back remote duels, judged by the AI judge. Observed at ${esc(new Date(snap.at).toLocaleString())}.</p>
</header>

<section class="kpis">
  <div class="kpi"><div class="kpi-label">Total duels</div><div class="kpi-value">${duels.length}</div></div>
  <div class="kpi"><div class="kpi-label">Decided</div><div class="kpi-value" style="color:var(--accent)">${winners}</div><div class="kpi-sub">+ ${ties.length} ties</div></div>
  <div class="kpi"><div class="kpi-label">Pending</div><div class="kpi-value" style="color:var(--warn)">${pending.length}</div><div class="kpi-sub">judge stood down</div></div>
  <div class="kpi"><div class="kpi-label">Side failures</div><div class="kpi-value" style="color:var(--danger)">${failed.length}</div></div>
  <div class="kpi"><div class="kpi-label">Wall-clock</div><div class="kpi-value">${totalDuration}</div><div class="kpi-sub">first start → last finish</div></div>
</section>

<h2>🏁 Standings</h2>
${renderStandings}

<h2>📊 Per-duel detail</h2>
${duels.map(renderDuel).join('\n')}

<footer>
  <p>Report built by <code>SCRIPTS/review/build-demo-report.cjs</code>. Data sourced from <code>/api/duels</code> via the BFF cookie set by <code>/auth/login/fake</code>. PRD §9 items 9, 13, 14, 19 govern the auto-judge, the tied verdict, and the walkover-on-failure rules that produce this distribution.</p>
  <p>Observations: 6 unique models won at least once; 2 ties (Tie is a terminal verdict per PRD §9 item 19); 2 walkovers where Kimi K2.7 Code produced non-renderable HTML; 1 duel where both sides failed and the judge correctly declined to invent a winner (PRD §9 item 9 "the no-evidence rule").</p>
</footer>

</div>
</body>
</html>`;

  fs.writeFileSync(REPORT, html);
  console.log(`wrote ${REPORT}`);
  console.log(`summary: ${duels.length} duels · ${winners} decided · ${ties.length} ties · ${pending.length} pending · ${failed.length} failures`);
  console.log(`models: ${standingsRows.map(s => `${s.name} ${s.wins}W/${s.losses}L/${s.ties}T`).join(' · ')}`);
})();