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
// Error classification — turn cryptic WebGPU/Dawn messages into a clear,
// actionable reason plus the phase it happened in, so failures are legible.
// ---------------------------------------------------------------------------
function classifyWebLlmError(rawMessage, phase) {
    const m = (rawMessage || '').toLowerCase();
    const where = phase === 'load' ? ' while loading the model'
                : phase === 'generate' ? ' during generation'
                : '';
    if (m.includes('external instance reference') || m.includes('device was lost') || m.includes('device is lost') || m.includes('device lost')) {
        return `GPU device was lost${where}. This usually means the GPU ran out of memory or a previous model's GPU context is still held. Hard-refresh (Ctrl+Shift+R) to clear WebGPU state, run one model at a time, and try a smaller model if it recurs.`;
    }
    if (m.includes('out of memory') || m.includes('oom') || m.includes('vram') || m.includes('failed to allocate') || m.includes('allocation')) {
        return `Ran out of GPU memory${where}. This model needs more VRAM than is available — pick a smaller model, or close other GPU-heavy tabs and retry.`;
    }
    if (m.includes('requestadapter') || m.includes('no adapter') || m.includes('adapter is null') || m.includes('gpu adapter') || (m.includes('webgpu') && m.includes('not'))) {
        return `WebGPU is not available in this browser${where}. Use desktop Chrome or Edge with hardware acceleration enabled.`;
    }
    if (m.includes('shader-f16') || m.includes('shader f16') || m.includes('f16')) {
        return `Your GPU lacks the shader-f16 feature this model requires${where}. Try a different device or browser.`;
    }
    if (m.includes('importscripts') || m.includes('failed to fetch') || m.includes('err_') || m.includes('networkerror') || m.includes('load failed')) {
        return `Could not download the model files${where}. Models stream from HuggingFace/CDN on first run — check your internet connection and retry.`;
    }
    if (m.includes('shader') || m.includes('compil')) {
        return `GPU shader compilation failed${where}. Your GPU or driver may not support the features this model needs.`;
    }
    if (m.includes('model_lib') || m.includes('model lib') || m.includes('cannot find model')) {
        return `This model isn't supported by the in-browser runner (missing model library)${where}.`;
    }
    return `${rawMessage || 'Unknown WebLLM error'}${where}.`;
}

// ---------------------------------------------------------------------------
// Main message handler
// ---------------------------------------------------------------------------
self.onmessage = async (event) => {
    const { modelId, webLlmModelId: wlmId, prompt, localModelBaseUrl } = event.data;
    // wlmId is the actual WebLLM model identifier (e.g. "Phi-3.5-mini-instruct-q4f32_1-MLC");
    // modelId is the internal ULID used only for routing status callbacks back to Blazor.
    const effectiveModelId = wlmId || modelId;
    let phase = 'init';

    console.log(`[WebLLM Worker] ▶ Start inference — modelId="${modelId}" effectiveModelId="${effectiveModelId}" localModelBaseUrl="${localModelBaseUrl}"`);

    // Log the GPU environment up-front so device-loss / OOM failures are diagnosable.
    try {
        if (typeof navigator !== 'undefined' && navigator.gpu) {
            const ad = await navigator.gpu.requestAdapter();
            if (ad) {
                let info = {};
                try { info = ad.info || (ad.requestAdapterInfo ? await ad.requestAdapterInfo() : {}); } catch (_) { /* ignore */ }
                const lim = ad.limits || {};
                console.log(`[WebLLM Worker] 🖥 GPU adapter — vendor="${info.vendor || '?'}" arch="${info.architecture || '?'}" device="${info.device || '?'}" ` +
                    `maxBufferSize=${lim.maxBufferSize ?? '?'} maxStorageBufferBindingSize=${lim.maxStorageBufferBindingSize ?? '?'} ` +
                    `features=[${ad.features ? [...ad.features].join(', ') : ''}]`);
            } else {
                console.warn('[WebLLM Worker] ⚠ navigator.gpu.requestAdapter() returned no adapter.');
            }
        } else {
            console.warn('[WebLLM Worker] ⚠ navigator.gpu not available in this worker context.');
        }
    } catch (probeErr) {
        console.warn(`[WebLLM Worker] ⚠ GPU probe failed: ${probeErr?.message ?? probeErr}`);
    }

    const startMs = performance.now();

    try {
        phase = 'load';
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

        phase = 'generate';
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
        const raw = err?.message ?? String(err);
        const friendly = classifyWebLlmError(raw, phase);
        const elapsedMs = Math.round(performance.now() - startMs);
        // Full context to the dev console…
        console.error(
            `[WebLLM Worker] ❌ FAILED (phase="${phase}") — model="${effectiveModelId}" ` +
            `cachedModel="${cachedModelId}" elapsedMs=${elapsedMs}\n` +
            `  reason: ${friendly}\n  raw: ${raw}`,
            err);
        // …and a clear, actionable reason to the UI, with the raw string preserved for
        // the "Show technical details" expander.
        self.postMessage({
            type: 'error',
            reason: `${friendly}\n\n[technical] phase=${phase} · model=${effectiveModelId} · ${raw}`,
            rawError: raw,
            phase,
        });
    }
};
