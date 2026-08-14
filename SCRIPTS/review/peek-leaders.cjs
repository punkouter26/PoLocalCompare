// Quick leaderboard peek for the report review.
const BASE = process.env.BASE_URL || 'https://localhost:5001';
async function seedAuth() {
  const r = await fetch(`${BASE}/auth/login/fake?user=GUEST_PEEK&returnUrl=/`, { redirect: 'manual' });
  const sc = r.headers.getSetCookie?.() ?? r.headers.get('set-cookie');
  return (Array.isArray(sc) ? sc[0] : sc)?.split(';')[0];
}
(async () => {
  const cookie = await seedAuth();
  const r = await fetch(`${BASE}/api/leaderboard`, { headers: { cookie } });
  const data = await r.json();
  const entries = Array.isArray(data) ? data : (data.entries ?? []);
  console.log(JSON.stringify(entries.map(e => ({
    name: e.modelName, elo: e.currentElo, W: e.winCount, L: e.lossCount, D: e.drawCount, n: e.duelCount,
  })), null, 2));
})();