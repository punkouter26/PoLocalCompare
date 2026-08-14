// Watches the demo via /api/duels until 10 duels are recorded (with verdicts), then dumps
// every relevant field. Used to drive the post-demo HTML report.
//
// Run: node SCRIPTS/review/watch-demo.cjs [--timeout=900] [--until=10]
//
// Authentication: hits /auth/login/fake first to set the BFF cookie. The cookie is then
// captured from the response and replayed against /api/duels. Without a cookie the API
// returns 401 (PRD §7), which is why we can't just call the endpoint anonymously.

const fs = require('fs');
const path = require('path');
// Node 24 has built-in fetch; use it instead of undici.

const BASE = process.env.BASE_URL || 'https://localhost:5001';
const ARGS = Object.fromEntries(process.argv.slice(2).map(a => a.replace(/^--/, '').split('=')));
const TIMEOUT_SEC = +ARGS.timeout || 900; // 15 minutes
const UNTIL = +ARGS.until || 10;

const OUT_DIR = path.join(__dirname, 'out', 'demo-run');
fs.mkdirSync(OUT_DIR, { recursive: true });
const PROGRESS = path.join(OUT_DIR, 'progress.json');

async function seedAuth() {
  const r = await fetch(`${BASE}/auth/login/fake?user=GUEST_REVIEW_WATCH&returnUrl=/`, {
    redirect: 'manual',
    headers: { accept: 'application/json' },
  });
  // Set-Cookie may be combined; take the first segment.
  const setCookie = r.headers.getSetCookie?.() ?? r.headers.get('set-cookie');
  const cookie = (Array.isArray(setCookie) ? setCookie[0] : setCookie)?.split(';')[0];
  await r.body.cancel?.();
  if (!cookie) throw new Error('No cookie returned from /auth/login/fake');
  return cookie;
}

async function listDuels(cookie) {
  const r = await fetch(`${BASE}/api/duels?limit=100`, {
    headers: { cookie, accept: 'application/json' },
  });
  if (!r.ok) throw new Error(`listDuels ${r.status}`);
  return await r.json();
}

async function getDuel(cookie, id) {
  const r = await fetch(`${BASE}/api/duels/${id}`, {
    headers: { cookie, accept: 'application/json' },
  });
  if (!r.ok) throw new Error(`getDuel ${r.status}`);
  return await r.json();
}

(async () => {
  const cookie = await seedAuth();
  console.log('[watch] auth OK');

  const deadline = Date.now() + TIMEOUT_SEC * 1000;
  let lastReport = null;

  while (Date.now() < deadline) {
    try {
      const list = await listDuels(cookie);
      // Demo duels are the most recent ones, paired with the demo's known prompts.
      const recent = list.slice(0, UNTIL);
      const decided = recent.filter(d => d.verdict && d.verdict !== 'Pending' && d.verdict !== 'Expired');

      // Persist progress.
      const snap = {
        at: new Date().toISOString(),
        list: recent,
        decidedCount: decided.length,
      };
      fs.writeFileSync(PROGRESS, JSON.stringify(snap, null, 2));
      console.log(`[watch] ${decided.length}/${UNTIL} decided · ${recent.length} recent duels`);

      // Fetch full details for each decided duel.
      if (decided.length > 0) {
        const detailed = await Promise.all(decided.map(d => getDuel(cookie, d.duelId).catch(e => ({ error: e.message, duelId: d.duelId }))));
        lastReport = { decided: detailed, observedAt: new Date().toISOString() };
      }

      if (decided.length >= UNTIL) {
        console.log('[watch] all duels decided');
        break;
      }
    } catch (err) {
      console.error('[watch] error:', err.message);
    }

    await new Promise(r => setTimeout(r, 8000));
  }

  fs.writeFileSync(path.join(OUT_DIR, 'final.json'), JSON.stringify(lastReport, null, 2));
  console.log('[watch] wrote final.json');
})();