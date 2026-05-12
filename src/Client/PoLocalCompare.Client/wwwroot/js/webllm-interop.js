/**
 * T051 — WebLLM JS interop glue
 * Bridging between Blazor WebLlmService.cs and the webllm-worker.js Web Worker.
 * Supports concurrent dual-model inference (e.g. two local models in a duel).
 */

// Map of modelId → Worker, allowing concurrent local model inference
const workers = {};
const availabilityCache = new Map();
const availabilityInflight = new Map();
const AVAILABILITY_TTL_MS = 15000;

function normalizeModelBaseUrl(baseUrl) {
    if (!baseUrl) {
        return '';
    }

    return baseUrl.endsWith('/') ? baseUrl : `${baseUrl}/`;
}

function buildCdnModelBaseUrl(webLlmModelId, cdnBaseUrlTemplate) {
    if (!cdnBaseUrlTemplate || !webLlmModelId) {
        return '';
    }

    if (cdnBaseUrlTemplate.includes('{modelId}')) {
        return normalizeModelBaseUrl(cdnBaseUrlTemplate.replaceAll('{modelId}', encodeURIComponent(webLlmModelId)));
    }

    return normalizeModelBaseUrl(`${cdnBaseUrlTemplate}${encodeURIComponent(webLlmModelId)}`);
}

async function canReachModelConfig(modelBaseUrl) {
    if (!modelBaseUrl) {
        return false;
    }

    try {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 3500);
        const response = await fetch(`${modelBaseUrl}mlc-chat-config.json`, {
            method: 'HEAD',
            signal: controller.signal,
            cache: 'no-store'
        });
        clearTimeout(timeout);
        return response.ok;
    } catch {
        return false;
    }
}

function getCdnTemplates(cdnBaseUrlTemplates) {
    if (!cdnBaseUrlTemplates) {
        return [];
    }

    if (Array.isArray(cdnBaseUrlTemplates)) {
        return cdnBaseUrlTemplates.filter(Boolean);
    }

    return [cdnBaseUrlTemplates];
}

window.resolveBrowserModelAvailability = async function (webLlmModelId, localModelBaseUrl, cdnBaseUrlTemplates) {
    const cacheKey = `${webLlmModelId}|${localModelBaseUrl}|${getCdnTemplates(cdnBaseUrlTemplates).join('|')}`;
    const cached = availabilityCache.get(cacheKey);
    if (cached && Date.now() - cached.at < AVAILABILITY_TTL_MS) {
        return cached.value;
    }

    if (availabilityInflight.has(cacheKey)) {
        return availabilityInflight.get(cacheKey);
    }

    const resolvePromise = (async () => {
    const localBaseUrl = normalizeModelBaseUrl(`${normalizeModelBaseUrl(localModelBaseUrl)}${webLlmModelId}`);
    if (await canReachModelConfig(localBaseUrl)) {
        return { available: true, source: 'local', baseUrl: localBaseUrl };
    }

    const templates = getCdnTemplates(cdnBaseUrlTemplates);
    for (let i = 0; i < templates.length; i++) {
        const cdnBaseUrl = buildCdnModelBaseUrl(webLlmModelId, templates[i]);
        if (await canReachModelConfig(cdnBaseUrl)) {
            return {
                available: true,
                source: i === 0 ? 'cdn' : 'cdn-backup',
                baseUrl: cdnBaseUrl
            };
        }
    }

    return { available: false, source: '', baseUrl: localBaseUrl };
    })();

    availabilityInflight.set(cacheKey, resolvePromise);
    try {
        const result = await resolvePromise;
        availabilityCache.set(cacheKey, { at: Date.now(), value: result });
        return result;
    } finally {
        availabilityInflight.delete(cacheKey);
    }
};

/**
 * Called from Blazor WebLlmService via IJSRuntime.InvokeVoidAsync("startWebLlmInference", dotnetRef, modelId, webLlmModelId, prompt)
 */
window.startWebLlmInference = async function (dotnetRef, modelId, webLlmModelId, prompt, cdnBaseUrlTemplates) {
    // Terminate any previous worker for this modelId
    if (workers[modelId]) {
        workers[modelId].terminate();
        delete workers[modelId];
    }

    const worker = new Worker('/js/webllm-worker.js?v=3', { type: 'module' });
    workers[modelId] = worker;

    worker.onmessage = (event) => {
        const msg = event.data;
        if (msg.type === 'status') {
            dotnetRef.invokeMethodAsync('ReceiveStatusUpdate', modelId, msg.status, msg.tokenCount, msg.elapsedMs,
                msg.detail ?? null,
                msg.htmlTagCount ?? 0,
                msg.openTagDepth ?? 0,
                msg.styleRuleCount ?? 0,
                msg.repetitionScore ?? 0,
                msg.prefillSpeedTps ?? 0,
                msg.cacheHit ?? false,
                msg.htmlPreview ?? null);
        } else if (msg.type === 'complete') {
            dotnetRef.invokeMethodAsync('ReceiveComplete', modelId, msg.htmlOutput, msg.tokenCount, msg.totalMs, msg.warmUpMs);
            delete workers[modelId];
        } else if (msg.type === 'error') {
            dotnetRef.invokeMethodAsync('ReceiveError', modelId, msg.reason);
            delete workers[modelId];
        }
    };

    worker.onerror = (err) => {
        dotnetRef.invokeMethodAsync('ReceiveError', modelId, err.message || 'Web Worker error');
        delete workers[modelId];
    };

    // Pass the origin-relative models base so the worker can self-host models
    const availability = await window.resolveBrowserModelAvailability(
        webLlmModelId,
        window.location.origin + '/models/',
        cdnBaseUrlTemplates);

    worker.postMessage({ modelId, webLlmModelId, prompt, localModelBaseUrl: availability.baseUrl });
};
