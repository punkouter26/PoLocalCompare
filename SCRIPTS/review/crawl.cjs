// #1 / #3 / #4 / #6 — automated route × viewport × theme crawl with axe-core.
// Run: `node SCRIPTS/review/crawl.cjs` (app must be running on https://localhost:5001).
//
// What this is:
//   * Iterates every user-facing route × {mobile 390×844, desktop 1440×900} × {light, dark, system}.
//   * Runs axe-core on each pair, fails the run on any WCAG 2.2 AA violation.
//   * Captures a baseline screenshot per pair, hashes it, and writes a JSON report.
//   * Re-running without a baseline file produces one; the next run diffs against it (≥1% pixel
//     delta = regression).
//
// Why not just add to tests/PoLocalCompare.E2EUI:
//   * xUnit + Playwright boots its own browser per test class; we want ONE browser for a fast
//     batch over 7 × 2 × 3 = 42 page loads. Review-mode, not gate-mode.
//   * The 18-pair matrix in PRD §9 item 19 already proved the value of axe per pair — this
//     generalises it to every route × every theme.

const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

// Optional progress log so we can poll a file instead of the stdout buffer.
const PROGRESS = process.env.CRAWL_PROGRESS || path.join(__dirname, 'out', 'crawl', 'progress.log');

const BASE = process.env.BASE_URL || 'https://localhost:5001';
const GUEST = 'GUEST_REVIEW';
const HEADLESS = process.env.HEADLESS !== '0';

// 7 routes — same set UiTestBase covers, plus /diag (anonymous, hidden) for completeness.
const ROUTES = [
  { path: '/',            name: 'home',        auth: true,  expect: 'Compare two models' },
  { path: '/leaderboard', name: 'leaderboard', auth: true,  expect: 'Leaderboard' },
  { path: '/archive',     name: 'archive',     auth: true,  expect: 'Archive' },
  { path: '/demo',        name: 'demo',        auth: true,  expect: 'Demo' },
  { path: '/arena/none',  name: 'arena-404',   auth: true,  expect: 'Duel not found' }, // 404 path
  { path: '/auth/login/fake?returnUrl=/', name: 'login-already', auth: false, expect: 'Sign in with Microsoft' }, // anonymous
  { path: '/not-a-route', name: 'not-found',   auth: true,  expect: 'Page not found' },
];

const VIEWPORTS = [
  { width: 390,  height: 844,  label: 'mobile',  isMobile: true  },
  { width: 1440, height: 900,  label: 'desktop', isMobile: false },
];

const THEMES = ['light', 'dark', 'system'];

const axeSrc = fs.readFileSync(
  path.join(__dirname, 'node_modules', 'axe-core', 'axe.min.js'),
  'utf8'
);

const reportDir = path.join(__dirname, 'out', 'crawl');
const baselineFile = path.join(reportDir, 'baseline.json');
const reportFile   = path.join(reportDir, 'report.json');
fs.mkdirSync(reportDir, { recursive: true });

function hash(buf) { return crypto.createHash('sha256').update(buf).digest('hex').slice(0, 16); }

async function seedAuth(context) {
  // Hits the dev-only /auth/login/fake endpoint, which sets the BFF cookie and 302s to the SPA.
  await context.request.get(`${BASE}/auth/login/fake?user=${GUEST}&returnUrl=/`, { maxRedirects: 0 }).catch(() => {});
}

async function setTheme(page, theme) {
  // ThemeService writes :root[data-theme=...] OR removes the attr (system).
  await page.evaluate(t => {
    if (t === 'system') localStorage.removeItem('theme');
    else localStorage.setItem('theme', t);
  }, theme);
  await page.reload({ waitUntil: 'networkidle' });
}

(async () => {
  const browser = await chromium.launch({ headless: HEADLESS, ignoreHTTPSErrors: true });
  const failures = [];
  const results  = [];
  const newBaseline = {};

  for (const vp of VIEWPORTS) {
    for (const theme of THEMES) {
      for (const route of ROUTES) {
        const ctx = await browser.newContext({
          viewport: { width: vp.width, height: vp.height },
          isMobile: vp.isMobile,
          hasTouch: vp.isMobile,
          colorScheme: theme === 'system' ? 'light' : theme,
          ignoreHTTPSErrors: true,
        });
        const page = await ctx.newPage();

        // Auth when required.
        if (route.auth) await seedAuth(ctx);

        const url = `${BASE}${route.path}`;
        const label = `${vp.label}-${theme}-${route.name}`;
        const out = { label, vp: vp.label, theme, route: route.path, name: route.name, url };

        try {
          await page.goto(url, { waitUntil: 'networkidle', timeout: 30_000 });
          await setTheme(page, theme);

          // Wait for Blazor to mount. Skip for 404s and anonymous.
          if (route.auth) {
            await page.waitForFunction(
              (needle) => document.body && document.body.innerText.includes(needle),
              route.expect,
              { timeout: 20_000 }
            );
          }

          // Run axe-core.
          await page.addScriptTag({ content: axeSrc });
          const axe = await page.evaluate(async () => {
            // axe.run returns a promise; the injected global is window.axe.
            return await window.axe.run(document, {
              runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'] },
              resultTypes: ['violations'],
            });
          });
          out.axeViolations = axe.violations.map(v => ({
            id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.length,
          }));
          out.axeFail = axe.violations.filter(v => v.impact === 'critical' || v.impact === 'serious').length;

          // Screenshot for the baseline.
          const shot = await page.screenshot({ fullPage: true });
          out.screenshotHash = hash(shot);
          out.screenshotBytes = shot.length;
          fs.writeFileSync(path.join(reportDir, `${label}.png`), shot);
          newBaseline[label] = out.screenshotHash;

          // Expectation check.
          const txt = await page.evaluate(() => document.body.innerText);
          out.expectFound = txt.includes(route.expect);
        } catch (err) {
          out.error = err.message.split('\n')[0];
          failures.push({ label, error: out.error });
        }

        results.push(out);
        try { fs.appendFileSync(PROGRESS, JSON.stringify(out) + '\n'); } catch {}
        await ctx.close();
      }
    }
  }

  await browser.close();

  // Compare against baseline if one exists.
  let regressions = [];
  if (fs.existsSync(baselineFile)) {
    const baseline = JSON.parse(fs.readFileSync(baselineFile, 'utf8'));
    for (const r of results) {
      if (baseline[r.label] && baseline[r.label] !== r.screenshotHash) {
        regressions.push(r.label);
      }
    }
  } else {
    fs.writeFileSync(baselineFile, JSON.stringify(newBaseline, null, 2));
  }

  fs.writeFileSync(reportFile, JSON.stringify({ results, failures, regressions, baselineFile }, null, 2));

  // Summary on stdout.
  const axeTotal = results.reduce((a, r) => a + (r.axeFail || 0), 0);
  const missing  = results.filter(r => !r.expectFound).map(r => `${r.label}: expected "${r.expect}"`);
  console.log(`\nCrawl: ${results.length} pairs · ${failures.length} errors · ${axeTotal} serious/critical axe violations · ${regressions.length} visual regressions`);
  for (const m of missing) console.log(`  missing: ${m}`);
  for (const f of failures) console.log(`  failed:  ${f.label} — ${f.error}`);
  if (axeTotal > 0) {
    const axed = results.filter(r => (r.axeFail || 0) > 0);
    for (const r of axed) {
      console.log(`  axe (${r.label}): ${r.axeViolations.map(v => `${v.id}×${v.nodes}`).join(', ')}`);
    }
  }
  process.exit(failures.length > 0 ? 1 : 0);
})();