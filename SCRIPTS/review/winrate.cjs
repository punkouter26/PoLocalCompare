const BASE = process.env.BASE_URL || 'https://localhost:5001';
(async () => {
  const r = await fetch(`${BASE}/auth/login/fake?user=G&returnUrl=/`, { redirect: 'manual' });
  const sc = r.headers.getSetCookie?.() ?? r.headers.get('set-cookie');
  const cookie = (Array.isArray(sc) ? sc[0] : sc).split(';')[0];
  const r2 = await fetch(`${BASE}/api/leaderboard?sortBy=Elo`, { headers: { cookie } });
  const data = await r2.json();
  const list = Array.isArray(data) ? data : data.entries;
  const rates = list.map(e => ({
    name: e.displayName,
    W: e.winCount,
    L: e.duelCount - e.winCount - e.drawCount,
    D: e.drawCount,
    rate: e.winRate,
    computed: e.winCount / Math.max(1, e.duelCount),
  }));
  console.log(JSON.stringify(rates, null, 2));
})();