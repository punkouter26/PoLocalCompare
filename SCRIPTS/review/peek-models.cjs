const BASE = process.env.BASE_URL || 'https://localhost:5001';
(async () => {
  const r = await fetch(`${BASE}/auth/login/fake?user=G&returnUrl=/`, { redirect: 'manual' });
  const sc = r.headers.getSetCookie?.() ?? r.headers.get('set-cookie');
  const cookie = (Array.isArray(sc) ? sc[0] : sc).split(';')[0];
  const data = await (await fetch(`${BASE}/api/models`, { headers: { cookie } })).json();
  console.log(JSON.stringify(data.filter(x => /codestral|llama|qwen|grok|mistral|phi-4|gpt/i.test(x.displayName)).map(x => ({
    name: x.displayName, type: x.modelType, endpoint: x.apiEndpointRef, webLlmModelId: x.webLlmModelId,
  })), null, 2));
})();