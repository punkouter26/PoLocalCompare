/**
 * T050 — WebLLM Web Worker
 * Loaded by webllm-interop.js via new Worker(). Receives {modelId, prompt} via postMessage.
 * Emits typed messages back to the main thread.
 */

importScripts('https://cdn.jsdelivr.net/npm/@mlc-ai/web-llm/dist/web-llm.js');

let engine = null;
let cachedModelId = null;

// ---------------------------------------------------------------------------
// HTML stats helpers
// ---------------------------------------------------------------------------
function computeHtmlStats(text) {
    let tagCount = 0, depth = 0, styleRules = 0;
    let inStyle = false;
    for (let i = 0; i < text.length; i++) {
        if (text[i] === '<') {
            const rest = text.slice(i + 1);
            if (rest.startsWith('/')) {
                depth--;
                if (/^\/style/i.test(rest)) inStyle = false;
            } else if (!rest.startsWith('!')) {
                tagCount++;
                depth++;
                if (/^style/i.test(rest)) inStyle = true;
            }
        } else if (inStyle && text[i] === '{') {
            styleRules++;
        }
    }
    return { tagCount, openDepth: Math.max(0, depth), styleRules };
}

function computeRepetitionScore(text) {
    const words = text.trim().split(/\s+/);
    if (words.length < 10) return 0;
    const n = 5;
    const seen = new Set();
    let duplicates = 0;
    for (let i = 0; i <= words.length - n; i++) {
        const gram = words.slice(i, i + n).join(' ');
        if (seen.has(gram)) duplicates++;
        else seen.add(gram);
    }
    const total = words.length - n + 1;
    return total > 0 ? Math.round((duplicates / total) * 100) / 100 : 0;
}

// ---------------------------------------------------------------------------
// Main message handler
// ---------------------------------------------------------------------------
self.onmessage = async (event) => {
    const { modelId, prompt, localModelBaseUrl } = event.data;

    const startMs = performance.now();

    try {
        self.postMessage({ type: 'status', status: 'Initializing', tokenCount: 0, elapsedMs: 0 });

        const isAlreadyCached = cachedModelId === modelId;

        if (!engine || !isAlreadyCached) {
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
            cachedModelId = modelId;
        }

        const warmUpMs = Math.round(performance.now() - startMs);
        const cacheHit = isAlreadyCached; // weights were already in memory

        // Parse prefill speed from runtimeStatsText if available
        let prefillSpeedTps = null;
        try {
            const statsText = await engine.runtimeStatsText();
            // Format: "prefill: 123.4 tok/s, decoding: 56.7 tok/s"
            const prefillMatch = statsText.match(/prefill[:\s]+([\d.]+)\s*tok\/s/i);
            if (prefillMatch) prefillSpeedTps = parseFloat(prefillMatch[1]);
        } catch (_) { /* runtimeStatsText not always available pre-generation */ }

        self.postMessage({
            type: 'status', status: 'Generating', tokenCount: 0, elapsedMs: warmUpMs,
            cacheHit, prefillSpeedTps,
        });

        let tokenCount = 0;
        let generatedContent = '';
        let lastStatusMs = 0;
        // Rolling window of last 200 chars for repetition detection
        const repWindow = [];
        const REP_WINDOW_CHARS = 200;

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

                    // HTML stats on full content (fast for typical page sizes)
                    const htmlStats = computeHtmlStats(generatedContent);

                    // Repetition on last ~200 chars
                    const repSample = generatedContent.slice(-REP_WINDOW_CHARS * 4);
                    const repetitionScore = computeRepetitionScore(repSample);

                    self.postMessage({
                        type: 'status', status: 'Generating',
                        tokenCount, elapsedMs,
                        htmlTagCount: htmlStats.tagCount,
                        openTagDepth: htmlStats.openDepth,
                        styleRuleCount: htmlStats.styleRules,
                        repetitionScore,
                        cacheHit,
                        prefillSpeedTps,
                        htmlPreview: generatedContent,
                    });
                }
            }
        }

        // Final prefill stats after generation
        try {
            const statsText = await engine.runtimeStatsText();
            const prefillMatch = statsText.match(/prefill[:\s]+([\d.]+)\s*tok\/s/i);
            if (prefillMatch) prefillSpeedTps = parseFloat(prefillMatch[1]);
        } catch (_) { }

        const totalMs = Math.round(performance.now() - startMs);
        self.postMessage({
            type: 'complete',
            htmlOutput: generatedContent,
            tokenCount,
            totalMs,
            warmUpMs,
            prefillSpeedTps,
            cacheHit,
        });
    } catch (err) {
        self.postMessage({ type: 'error', reason: err?.message ?? String(err) });
    }
};
