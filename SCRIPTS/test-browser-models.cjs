/**
 * Smoke-tests every browser (WebLLM/WebGPU) model by driving a real browser.
 *
 * Local models never execute on the server — DuelExecutionService signals the client over
 * SignalR and waits for it to POST the result back — so they cannot be tested by hitting the
 * API alone. That is why SCRIPTS/test-models-rotating-cube.ps1 can only mark them
 * NOT_TESTED_BROWSER_REQUIRED. This script is the missing half: it opens a page per model,
 * lets WebLLM run, and records what came back.
 *
 * It attaches to an already-running Chrome over CDP rather than launching one itself, and that
 * is not incidental. A Playwright-launched browser — bundled Chromium or `channel: 'chrome'` —
 * only ever exposes the SwiftShader fallback adapter here: `isFallbackAdapter: true`, no
 * `shader-f16`. Every q4f16 model would fail identically for a reason that has nothing to do
 * with the model, and the q4f32 ones would crawl. Chrome started normally gets the real GPU.
 * SCRIPTS/test-browser-models.ps1 starts that Chrome with the flags that pick the discrete GPU.
 *
 * Prerequisites:
 *   - App running at BASE_URL (default https://localhost:5001)
 *   - Weights present in wwwroot/models (SCRIPTS/download-models.py)
 *   - Playwright driver built: dotnet build tests/PoLocalCompare.E2EUI
 *   - Chrome listening on CDP_URL (the .ps1 wrapper handles this)
 *
 * Usage (from repo root):
 *   pwsh SCRIPTS/test-browser-models.ps1
 *
 * Env:
 *   BASE_URL     default https://localhost:5001
 *   CDP_URL      default http://127.0.0.1:9222
 *   ONLY         comma-separated display names or model ids to restrict the run
 *   TIMEOUT_MS   per-model ceiling, default 900000 (first run compiles WebGPU shaders)
 *   OUT          output TSV path, default browser-test-status.tsv
 */

const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn, execSync } = require('child_process');
const { chromium } = require(process.env.PW_PACKAGE || 'playwright-core');

const BASE_URL = (process.env.BASE_URL || 'https://localhost:5001').replace(/\/$/, '');
const CDP_URL = process.env.CDP_URL || 'http://127.0.0.1:9222';
/** Ceiling on a model's own inference, measured from when its duel actually starts. */
const TIMEOUT_MS = Number(process.env.TIMEOUT_MS || 900_000);
/** Separate ceiling on time spent waiting for the duel queue. Not charged to the model. */
const QUEUE_WAIT_MS = Number(process.env.QUEUE_WAIT_MS || 1_200_000);
const OUT = process.env.OUT || path.join(__dirname, '..', 'browser-test-status.tsv');
const ONLY = (process.env.ONLY || '').split(',').map(s => s.trim()).filter(Boolean);
const AUTH = { 'X-Fake-User': 'browser-model-test', 'X-Fake-Roles': 'User' };
const CHROME = process.env.CHROME_PATH;
const CDP_PORT = Number(process.env.CDP_PORT || 9222);
const PROMPT =
  'Build a single HTML page with a heading that says Hello, a red button labelled Click me, ' +
  'and an unordered list of exactly three fruits.';

const api = async (route, init = {}) => {
  const res = await fetch(`${BASE_URL}${route}`, {
    ...init,
    headers: { ...AUTH, 'Content-Type': 'application/json', ...(init.headers || {}) },
  });
  if (!res.ok) throw new Error(`${init.method || 'GET'} ${route} -> HTTP ${res.status}`);
  return res.status === 204 ? null : res.json();
};

const sleep = ms => new Promise(r => setTimeout(r, ms));
const clean = s => (s == null || s === '' ? '-' : String(s).replace(/[|\r\n]+/g, ' ').trim());

/**
 * Starts a dedicated Chrome and returns a Playwright handle to it.
 *
 * One browser per model, not one page per model. Closing a page does NOT release the model's
 * WebGPU context: running several models in a single Chrome exhausted the GPU and the fourth
 * one died with "GPU device was lost while loading the model — a previous model's GPU context
 * is still held". That happened at position four in two separate runs, which read like two
 * different model failures and was really one leak. A fresh process is the only reliable way
 * to hand each model the whole GPU.
 */
async function startBrowser(profileRoot, index) {
  const userDataDir = path.join(profileRoot, `p${index}`);
  fs.mkdirSync(userDataDir, { recursive: true });
  const port = CDP_PORT + (index % 50);

  const proc = spawn(CHROME, [
    `--remote-debugging-port=${port}`,
    `--user-data-dir=${userDataDir}`,
    '--no-first-run', '--no-default-browser-check',
    '--enable-unsafe-webgpu',       // exposes navigator.gpu
    '--force-high-performance-gpu', // discrete GPU rather than integrated
    '--ignore-gpu-blocklist',
    '--ignore-certificate-errors',  // the ASP.NET dev cert
    'about:blank',
  ], { stdio: 'ignore', detached: false });

  const deadline = Date.now() + 30_000;
  for (;;) {
    try {
      const r = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (r.ok) break;
    } catch { /* not listening yet */ }
    if (Date.now() > deadline) {
      try { proc.kill(); } catch { /* already gone */ }
      throw new Error(`Chrome did not open a CDP endpoint on port ${port}`);
    }
    await sleep(400);
  }

  const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  return { browser, proc, port };
}

async function stopBrowser(handle) {
  if (!handle) return;
  try { await handle.browser.close(); } catch { /* already detached */ }
  try { handle.proc.kill(); } catch { /* already gone */ }
  // Chrome spawns children; kill the tree so the GPU process cannot outlive the run.
  if (process.platform === 'win32') {
    try { execSync(`taskkill /PID ${handle.proc.pid} /T /F`, { stdio: 'ignore' }); } catch { /* gone */ }
  }
  await sleep(1500); // let the GPU driver reclaim before the next model asks for it
}

/**
 * Confirms the attached browser has a real GPU adapter before any model runs. A software
 * adapter would make every model fail for the same irrelevant reason, and shader-f16 is a hard
 * requirement for the q4f16 half of the roster.
 */
async function checkWebGpu(ctx) {
  const page = await ctx.newPage();
  try {
    await page.goto(`${BASE_URL}/`, { waitUntil: 'domcontentloaded' });
    return await page.evaluate(async () => {
      if (!navigator.gpu) return { ok: false, why: 'navigator.gpu is undefined' };
      const a = await navigator.gpu.requestAdapter({ powerPreference: 'high-performance' });
      if (!a) return { ok: false, why: 'requestAdapter() returned null' };
      const i = a.info || {};
      if (a.isFallbackAdapter) {
        return { ok: false, why: `software fallback adapter (${i.vendor}/${i.architecture}) — not a real GPU` };
      }
      return {
        ok: true,
        vendor: i.vendor || '?',
        arch: i.architecture || '?',
        f16: a.features.has('shader-f16'),
      };
    });
  } finally {
    await page.close();
  }
}

/**
 * Console noise that says nothing about the model. The live preview on /processing re-renders
 * the partial HTML as it streams, and models routinely invent asset URLs (`/style.css`,
 * `/static/angular.js`), so every re-render re-logs a failed sub-resource. One run produced
 * 11,687 of these. They are a property of the generated markup, not a model failure, and
 * reporting the first one verbatim made a crash look like an auth problem.
 */
const IRRELEVANT_ERROR =
  /Failed to load resource|net::ERR_|index\.png|\/css\/css\d+\.css|favicon|\/images\//i;

/** Keep diagnosis bounded: a runaway preview can log tens of thousands of identical errors. */
const MAX_ERRORS = 50;

/** Runs one duel with `model` on the browser side and `opponent` on the server side. */
async function testModel(ctx, model, opponent, log) {
  // A fresh page per model, closed afterwards, so each model gets its own worker and the
  // previous model's GPU allocation is released before the next one loads. (connectOverCDP
  // only exposes the default browser context, so isolation is per-page, not per-context.)
  const page = await ctx.newPage();
  const consoleErrors = [];
  let suppressed = 0;
  page.on('console', m => {
    if (m.type() !== 'error') return;
    const t = m.text().slice(0, 300);
    if (IRRELEVANT_ERROR.test(t)) { suppressed++; return; }
    if (consoleErrors.length < MAX_ERRORS) consoleErrors.push(t);
  });
  page.on('pageerror', e => {
    if (consoleErrors.length < MAX_ERRORS) consoleErrors.push(`pageerror: ${e.message}`.slice(0, 300));
  });
  page.on('crash', () => consoleErrors.push('the page crashed (renderer or GPU process died)'));

  const started = Date.now();
  try {
    // Authenticate before creating the duel. The server starts signalling the client the moment
    // the duel exists, so creating it first against a page that turns out to be dead just leaves
    // an orphan duel spinning against its 900 s watchdog.
    await page.goto(`${BASE_URL}/e2e/seed-auth?redirect=/`, { waitUntil: 'domcontentloaded' });

    const duel = await api('/api/duels', {
      method: 'POST',
      body: JSON.stringify({
        leftModelId: model.modelId,
        rightModelId: opponent.modelId,
        promptText: PROMPT,
      }),
    });

    await page.goto(`${BASE_URL}/processing/${duel.duelId}`, { waitUntil: 'domcontentloaded' });

    // Duels execute one at a time (BackgroundTaskService awaits each work item before dequeuing
    // the next) and an unattended local duel holds the queue for its full 900 s watchdog. So a
    // duel can sit queued long before it starts, and time spent queued must not be charged to
    // the model. The server-side opponent finishes within seconds of the duel being dequeued,
    // so its result appearing is the signal that this duel is genuinely running.
    let runningSince = null;

    for (;;) {
      const waited = Date.now() - started;
      if (runningSince === null && waited > QUEUE_WAIT_MS) {
        return {
          status: 'QUEUE_BLOCKED', tokens: 0, durationMs: waited, duelId: duel.duelId,
          reason: `duel never started within ${Math.round(QUEUE_WAIT_MS / 1000)}s — the duel ` +
                  `queue is blocked by an earlier duel, so this says nothing about the model`,
        };
      }
      if (runningSince !== null && Date.now() - runningSince > TIMEOUT_MS) break;

      const state = await api(`/api/duels/${duel.duelId}`);
      const results = state.results || [];
      if (runningSince === null && results.some(r => r.modelId === opponent.modelId)) {
        runningSince = Date.now();
        log(`    duel started after ${Math.round(waited / 1000)}s queued`);
      }

      const hit = results.find(r => r.modelId === model.modelId);
      if (hit) {
        return {
          status: hit.isFailure ? 'FAILS' : 'WORKS',
          tokens: hit.tokenCount,
          durationMs: hit.totalDurationMs,
          warmUpMs: hit.warmUpDurationMs,
          bytes: hit.htmlOutputSizeBytes,
          quality: hit.outputQualityScore,
          duelId: duel.duelId,
          reason: hit.isFailure ? hit.failureReason : null,
        };
      }
      if (page.isClosed()) {
        return {
          status: 'CRASHED', tokens: 0, durationMs: Date.now() - started, duelId: duel.duelId,
          reason: (consoleErrors[0] || 'the page closed before producing a result') +
                  (suppressed > 1000
                    ? ` (${suppressed} suppressed sub-resource errors — the live preview was ` +
                      `re-rendering generated markup that references non-existent assets)`
                    : ''),
        };
      }

      await sleep(2000);
      const secs = Math.round((Date.now() - started) / 1000);
      if (secs % 30 === 0) {
        log(`    …${secs}s${runningSince === null ? ' (still queued)' : ''}` +
            `${consoleErrors.length ? ` (${consoleErrors.length} errors)` : ''}` +
            `${suppressed ? ` (${suppressed} suppressed)` : ''}`);
      }
    }

    return {
      status: 'TIMEOUT',
      tokens: 0,
      durationMs: Date.now() - runningSince,
      duelId: duel.duelId,
      // Report every distinct error, not just the first — the first is rarely the cause.
      reason: consoleErrors.length
        ? `no result within ${Math.round(TIMEOUT_MS / 1000)}s; console: ${[...new Set(consoleErrors)].slice(0, 3).join(' // ')}`
        : `no result within ${Math.round(TIMEOUT_MS / 1000)}s and no console errors`,
    };
  } finally {
    await page.close().catch(() => {});
  }
}

(async () => {
  const log = m => { process.stdout.write(m + '\n'); };

  const models = await api('/api/models');
  let locals = models.filter(m => m.modelType === 'Local');
  if (ONLY.length) locals = locals.filter(m => ONLY.includes(m.displayName) || ONLY.includes(m.modelId));

  // The opponent runs server-side so a browser page only ever loads one model — a failure is
  // then unambiguously attributable to the model under test.
  const opponent = models.find(m => m.modelType === 'Remote' && /nano/i.test(m.displayName))
    || models.find(m => m.modelType === 'Remote');
  if (!opponent) throw new Error('no Remote model available to act as the server-side opponent');
  if (!locals.length) throw new Error('no Local models matched');

  log(`Base URL : ${BASE_URL}`);
  log(`Opponent : ${opponent.displayName} (${opponent.modelType}, server-side)`);
  log(`Models   : ${locals.length}\n`);

  if (!CHROME) throw new Error('CHROME_PATH is not set — run via SCRIPTS/test-browser-models.ps1');
  const profileRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'polocalcompare-webgpu-'));
  const rows = [];

  try {
    for (const [i, model] of locals.entries()) {
      log(`[${i + 1}/${locals.length}] ${model.displayName} (${model.webLlmModelId})`);
      let handle = null;
      let row;
      try {
        handle = await startBrowser(profileRoot, i);
        const ctx = handle.browser.contexts()[0];

        const gpu = await checkWebGpu(ctx);
        if (!gpu.ok) {
          // Not a model result: on a software adapter every model fails identically.
          throw new Error(`no usable GPU: ${gpu.why}`);
        }
        if (i === 0) {
          log(`    GPU: ${gpu.vendor} / ${gpu.arch}, shader-f16=${gpu.f16}`);
          if (!gpu.f16) log('    WARNING: shader-f16 missing — every q4f16 model will fail.');
        }

        row = await testModel(ctx, model, opponent, log);
      } catch (e) {
        row = { status: 'ERROR', tokens: 0, durationMs: 0, duelId: '-', reason: e.message };
      } finally {
        await stopBrowser(handle);
      }

      rows.push({ model, ...row });
      log(`    ${row.status}  tokens=${row.tokens}  ${Math.round((row.durationMs || 0) / 1000)}s` +
          (row.reason ? `  reason=${clean(row.reason)}` : '') + '\n');
    }
  } finally {
    fs.rmSync(profileRoot, { recursive: true, force: true, maxRetries: 3 });
  }

  const header = 'MODEL|WEBLLM_ID|TYPE|STATUS|TOKENS|WARMUP_MS|DURATION_MS|BYTES|QUALITY|DUEL_ID|FAILURE_REASON';
  const lines = rows.map(r => [
    r.model.displayName, r.model.webLlmModelId, r.model.modelType, r.status,
    r.tokens ?? 0, r.warmUpMs ?? 0, r.durationMs ?? 0, r.bytes ?? 0, r.quality ?? 0,
    r.duelId, clean(r.reason),
  ].join('|'));
  fs.writeFileSync(OUT, [header, ...lines].join('\n') + '\n', 'utf8');

  const worked = rows.filter(r => r.status === 'WORKS').length;
  log(`\n${worked}/${rows.length} models produced output. Wrote ${OUT}`);
  process.exit(worked === rows.length ? 0 : 1);
})().catch(e => { console.error('FATAL', e.message); process.exit(2); });
