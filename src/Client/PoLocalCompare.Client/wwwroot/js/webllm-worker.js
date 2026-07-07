/**
 * T050 — WebLLM Web Worker
 * Loaded by webllm-interop.js via new Worker(). Receives {modelId, prompt} via postMessage.
 * Emits typed messages back to the main thread.
 */

import * as webllm from '/js/web-llm.js';

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
    const { modelId, webLlmModelId: wlmId, prompt, localModelBaseUrl } = event.data;
    // wlmId is the actual WebLLM model identifier (e.g. "Phi-3.5-mini-instruct-q4f32_1-MLC");
    // modelId is the internal ULID used only for routing status callbacks back to Blazor.
    const effectiveModelId = wlmId || modelId;

    console.log(`[WebLLM Worker] ▶ Start inference — modelId="${modelId}" effectiveModelId="${effectiveModelId}" localModelBaseUrl="${localModelBaseUrl}"`);

    const startMs = performance.now();

    try {
        self.postMessage({ type: 'status', status: 'Initializing', tokenCount: 0, elapsedMs: 0, loadProgress: 0 });

        const isAlreadyCached = cachedModelId === effectiveModelId;
        console.log(`[WebLLM Worker] Cache check — cachedModelId="${cachedModelId}" isAlreadyCached=${isAlreadyCached}`);

        if (!engine || !isAlreadyCached) {
            // Look up model_lib (WASM URL) from prebuilt config so local models get the correct WASM library
            const prebuiltEntry = webllm.prebuiltAppConfig?.model_list?.find(m => m.model_id === effectiveModelId);
            // localModelBaseUrl already points at this model's root (resolveBrowserModelAvailability
            // probed <baseUrl>mlc-chat-config.json) — do not append the model id again.
            const appConfig = localModelBaseUrl
                ? { model_list: [{ model: localModelBaseUrl, model_id: effectiveModelId, model_lib: prebuiltEntry?.model_lib }] }
                : undefined;

            console.log(`[WebLLM Worker] Creating MLCEngine — effectiveModelId="${effectiveModelId}" appConfig=`, appConfig ?? '(none, using CDN)');

            engine = await webllm.CreateMLCEngine(effectiveModelId, {
                ...(appConfig ? { appConfig } : {}),
                initProgressCallback: (progress) => {
                    const elapsedMs = Math.round(performance.now() - startMs);
                    const loadProgress = Math.round((progress.progress ?? 0) * 100);
                    console.log(`[WebLLM Worker] Load progress ${loadProgress}% (${elapsedMs}ms) — ${progress.text}`);
                    self.postMessage({
                        type: 'status',
                        status: 'Initializing',
                        tokenCount: 0,
                        elapsedMs,
                        detail: progress.text,
                        loadProgress,
                    });
                },
            });
            cachedModelId = effectiveModelId;
            console.log(`[WebLLM Worker] ✅ Engine ready — model="${effectiveModelId}" warmUpMs=${Math.round(performance.now() - startMs)}`);
        } else {
            console.log(`[WebLLM Worker] ⚡ Using cached engine for "${effectiveModelId}"`);
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

        console.log(`[WebLLM Worker] 🚀 Generating — warmUpMs=${warmUpMs} cacheHit=${cacheHit} prefillSpeedTps=${prefillSpeedTps}`);
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

        // Same HTML-forcing system prompt the Foundry/Ollama proxies use — without it,
        // small local models answer conversationally ("I'm sorry, but as an AI…") instead
        // of emitting a page.
        const stream = await engine.chat.completions.create({
            messages: [
                { role: 'system', content: 'You are an expert HTML/CSS coder. Return only valid HTML5 with inline CSS. No markdown, no explanation, no code fences.' },
                { role: 'user', content: prompt },
            ],
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
        console.log(`[WebLLM Worker] ✅ Complete — tokenCount=${tokenCount} totalMs=${totalMs} warmUpMs=${warmUpMs} prefillSpeedTps=${prefillSpeedTps}`);
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
        console.error(`[WebLLM Worker] ❌ Error — ${err?.message ?? String(err)}`, err);
        self.postMessage({ type: 'error', reason: err?.message ?? String(err) });
    }
};
