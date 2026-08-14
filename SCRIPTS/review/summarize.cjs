// Aggregate the per-pair progress log into a single report.
//
// Reads SCRIPTS/review/out/crawl/progress.log (one JSON object per line) and emits a
// human-readable summary plus a machine-readable summary.json.
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, 'out', 'crawl');
const IN   = path.join(ROOT, 'progress.log');
const OUT  = path.join(ROOT, 'summary.json');
const MD   = path.join(ROOT, 'summary.md');

const lines = fs.readFileSync(IN, 'utf8').split('\n').filter(Boolean);
const rows = lines.map(JSON.parse);

// Categorize.
const failed = rows.filter(r => r.error);
const axeBad = rows.filter(r => (r.axeFail || 0) > 0);
const missing = rows.filter(r => !r.expectFound);

const byRoute = {};
for (const r of rows) {
  byRoute[r.route] ??= { axeBad: 0, failed: 0, missing: 0, total: 0 };
  byRoute[r.route].total++;
  byRoute[r.route].axeBad += r.axeFail || 0;
  if (r.error) byRoute[r.route].failed++;
  if (!r.expectFound) byRoute[r.route].missing++;
}

const byViewport = {};
for (const r of rows) {
  byViewport[r.vp] ??= { axeBad: 0, failed: 0, total: 0 };
  byViewport[r.vp].total++;
  byViewport[r.vp].axeBad += r.axeFail || 0;
  if (r.error) byViewport[r.vp].failed++;
}

const byTheme = {};
for (const r of rows) {
  byTheme[r.theme] ??= { axeBad: 0, failed: 0, total: 0 };
  byTheme[r.theme].total++;
  byTheme[r.theme].axeBad += r.axeFail || 0;
  if (r.error) byTheme[r.theme].failed++;
}

// The actual axe violation IDs we saw (so we know what to fix first).
const axeIds = {};
for (const r of rows) {
  for (const v of r.axeViolations || []) {
    axeIds[v.id] = (axeIds[v.id] || 0) + v.nodes;
  }
}

const summary = {
  totals: {
    pairs: rows.length,
    errors: failed.length,
    axeSeriousCritical: axeBad.reduce((a, r) => a + r.axeFail, 0),
    missingExpectation: missing.length,
  },
  byRoute, byViewport, byTheme, axeIds,
  failed: failed.map(r => ({ label: r.label, route: r.route, error: r.error })),
  missingExpectation: missing.map(r => ({ label: r.label, route: r.route })),
  axeByLabel: axeBad.map(r => ({ label: r.label, violations: r.axeViolations })),
};

fs.writeFileSync(OUT, JSON.stringify(summary, null, 2));

const md = [];
md.push('# Crawl summary\n');
md.push(`Pairs crawled: **${summary.totals.pairs}** of the planned 42.`);
md.push(`Errors: **${summary.totals.errors}**.`);
md.push(`Serious/critical axe violations: **${summary.totals.axeSeriousCritical}**.`);
md.push(`Pages where the expected text was not found: **${summary.totals.missingExpectation}**.\n`);

md.push('## By route\n');
md.push('| Route | Pairs | Axe bad | Errors | Missing expectation |');
md.push('|---|---|---|---|---|');
for (const [k, v] of Object.entries(byRoute).sort((a,b) => b[1].axeBad - a[1].axeBad)) {
  md.push(`| \`${k}\` | ${v.total} | ${v.axeBad} | ${v.failed} | ${v.missing} |`);
}

md.push('\n## By viewport\n');
md.push('| Viewport | Pairs | Axe bad | Errors |');
md.push('|---|---|---|---|');
for (const [k, v] of Object.entries(byViewport)) {
  md.push(`| ${k} | ${v.total} | ${v.axeBad} | ${v.failed} |`);
}

md.push('\n## By theme\n');
md.push('| Theme | Pairs | Axe bad | Errors |');
md.push('|---|---|---|---|');
for (const [k, v] of Object.entries(byTheme)) {
  md.push(`| ${k} | ${v.total} | ${v.axeBad} | ${v.failed} |`);
}

md.push('\n## Axe violation IDs seen\n');
md.push('| Rule | Node count |');
md.push('|---|---|');
for (const [k, v] of Object.entries(axeIds).sort((a,b) => b[1] - a[1])) {
  md.push(`| \`${k}\` | ${v} |`);
}

md.push('\n## Errors\n');
for (const r of failed) md.push(`- \`${r.label}\`: ${r.error}`);

md.push('\n## Pages where the expected marker was not found\n');
for (const r of missing) md.push(`- \`${r.label}\`: expected "${(rows.find(x => x.label === r.label).expect)}"`);

md.push('\n## Axe violations by pair\n');
for (const r of axeBad) {
  md.push(`- \`${r.label}\`: ${r.axeViolations.map(v => `\`${v.id}\`×${v.nodes}`).join(', ')}`);
}

fs.writeFileSync(MD, md.join('\n'));

console.log(fs.readFileSync(MD, 'utf8'));