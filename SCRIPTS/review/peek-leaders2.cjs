const BASE = process.env.BASE_URL || 'https://localhost:5001';
(async () => {
  const r = await fetch(`${BASE}/auth/login/fake?user=GUEST&returnUrl=/`, { redirect: 'manual' });
  const sc = r.headers.getSetCookie?.() ?? r.headers.get('set-cookie');
  const cookie = (Array.isArray(sc) ? sc[0] : sc)?.split(';')[0];
  const r2 = await fetch(`${BASE}/api/leaderboard`, { headers: { cookie } });
  const data = await r2.json();
  console.log('Top-level type:', Array.isArray(data) ? 'array' : typeof data, '| keys:', Object.keys(data));
  const list = Array.isArray(data) ? data : (data.entries ?? []);
  console.log('First entry full keys:', Object.keys(list[0] ?? {}));
  console.log('First entry:', JSON.stringify(list[0], null, 2));
  console.log('Total entries:', list.length);
})();