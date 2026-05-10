/**
 * T050 — WebLLM Web Worker
 * Loaded by webllm-interop.js via new Worker(). Receives {modelId, prompt} via postMessage.
 * Emits typed messages back to the main thread.
 */

importScripts('https://cdn.jsdelivr.net/npm/@mlc-ai/web-llm/dist/web-llm.js');

let engine = null;

self.onmessage = async (event) => {
    const { modelId, prompt, localModelBaseUrl } = event.data;

    const startMs = performance.now();

    try {
        self.postMessage({ type: 'status', status: 'Initializing', tokenCount: 0, elapsedMs: 0 });

        if (!engine || engine.currentModelId !== modelId) {
            // If local model files are served from the app, override the CDN URL
            const appConfig = localModelBaseUrl
                ? { model_list: [{ model: localModelBaseUrl + modelId + '/', model_id: modelId }] }
                : undefined;

            engine = await webllm.CreateMLCEngine(modelId, {
                ...(appConfig ? { appConfig } : {}),
                initProgressCallback: (progress) => {
                    const elapsedMs = Math.round(performance.now() - startMs);
                    self.postMessage({
                        type: 'status',
                        status: 'Initializing',
                        tokenCount: 0,
                        elapsedMs,
                        detail: progress.text,
                    });
                },
            });
        }

        const warmUpMs = Math.round(performance.now() - startMs);
        self.postMessage({ type: 'status', status: 'Generating', tokenCount: 0, elapsedMs: warmUpMs });

        let tokenCount = 0;
        let generatedContent = '';
        let lastStatusMs = 0;

        const stream = await engine.chat.completions.create({
            messages: [{ role: 'user', content: prompt }],
            stream: true,
            max_tokens: 8000,
        });

        for await (const chunk of stream) {
            const delta = chunk.choices[0]?.delta?.content ?? '';
            if (delta) {
                generatedContent += delta;
                tokenCount++;
                const elapsedMs = Math.round(performance.now() - startMs);
                // Emit status every 500ms
                if (elapsedMs - lastStatusMs >= 500) {
                    lastStatusMs = elapsedMs;
                    self.postMessage({ type: 'status', status: 'Generating', tokenCount, elapsedMs });
                }
            }
        }

        const totalMs = Math.round(performance.now() - startMs);
        self.postMessage({
            type: 'complete',
            htmlOutput: generatedContent,
            tokenCount,
            totalMs,
            warmUpMs,
        });
    } catch (err) {
        self.postMessage({ type: 'error', reason: err?.message ?? String(err) });
    }
};
