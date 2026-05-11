/**
 * diag-worker.js — Standalone WebLLM inference worker for the Local Model Lab.
 *
 * Receives: { diagId, webLlmModelId, localModelBaseUrl, prompt }
 *
 * Emits:
 *   { type: 'step',   diagId, step, detail, progress }
 *   { type: 'result', diagId, loadMs, firstTokenMs, tokensPerSec, totalTokens, output, error }
 */

importScripts('https://cdn.jsdelivr.net/npm/@mlc-ai/web-llm/dist/web-llm.js');

self.onmessage = async (event) => {
    const { diagId, webLlmModelId, localModelBaseUrl, prompt } = event.data;
    const t0 = performance.now();
    const ms = () => Math.round(performance.now() - t0);
    const emit = (type, extra) => self.postMessage({ type, diagId, ...extra });

    try {
        emit('step', { step: 'Loading', detail: 'Creating ML engine…', progress: 0 });

        const appConfig = {
            model_list: [{
                model: localModelBaseUrl + webLlmModelId + '/',
                model_id: webLlmModelId,
            }],
        };

        const engine = await webllm.CreateMLCEngine(webLlmModelId, {
            appConfig,
            initProgressCallback: (p) => {
                emit('step', {
                    step: 'Loading',
                    detail: p.text,
                    progress: Math.round((p.progress ?? 0) * 100),
                });
            },
        });

        const loadMs = ms();
        emit('step', {
            step: 'Generating',
            detail: `Loaded in ${(loadMs / 1000).toFixed(1)}s — running test prompt…`,
            progress: 100,
        });

        const genStart = performance.now();
        let firstTokenMs = -1;
        let tokenCount = 0;
        let output = '';

        const stream = await engine.chat.completions.create({
            messages: [{ role: 'user', content: prompt }],
            stream: true,
            max_tokens: 256,
        });

        for await (const chunk of stream) {
            const delta = chunk.choices[0]?.delta?.content ?? '';
            if (!delta) continue;

            if (firstTokenMs < 0) firstTokenMs = Math.round(performance.now() - genStart);
            tokenCount++;
            output += delta;

            if (tokenCount % 25 === 0) {
                const elapsed = performance.now() - genStart;
                const tps = Math.round((tokenCount / elapsed) * 1000);
                emit('step', {
                    step: 'Generating',
                    detail: `${tokenCount} tokens · ${tps} tok/s`,
                    progress: 100,
                });
            }
        }

        const genMs = Math.round(performance.now() - genStart);
        const tokensPerSec = genMs > 0 ? Math.round((tokenCount / genMs) * 1000) : 0;

        emit('result', {
            loadMs,
            firstTokenMs: firstTokenMs >= 0 ? firstTokenMs : genMs,
            tokensPerSec,
            totalTokens: tokenCount,
            output,
            error: null,
        });

    } catch (e) {
        emit('result', {
            loadMs: ms(),
            firstTokenMs: -1,
            tokensPerSec: 0,
            totalTokens: 0,
            output: '',
            error: e?.message ?? String(e),
        });
    }
};
