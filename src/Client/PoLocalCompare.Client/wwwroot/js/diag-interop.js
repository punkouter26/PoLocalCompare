/**
 * diag-interop.js — JS bridge for ModelHealthPanel.razor <-> webllm-worker.js
 */

const _diagWorkers = {};

// ── WebGPU detection ─────────────────────────────────────────────────────────
window.checkWebGpu = async function () {
    if (!navigator.gpu) {
        return {
            supported: false, vendor: '', architecture: '', device: '',
            reason: 'navigator.gpu not available — use Chrome 113+ or Edge 113+ on a WebGPU-capable device.',
        };
    }
    try {
        const adapter = await navigator.gpu.requestAdapter();
        if (!adapter) {
            return { supported: false, vendor: '', architecture: '', device: '', reason: 'No GPU adapter found.' };
        }
        let vendor = '', architecture = '', device = '';
        try {
            const info = adapter.info || (adapter.requestAdapterInfo ? await adapter.requestAdapterInfo() : {});
            vendor = info.vendor || '';
            architecture = info.architecture || '';
            device = info.device || '';
        } catch (_) { /* adapter info not available in all browsers */ }

        // "Has an adapter" is not enough — WebLLM needs real hardware plus the shader-f16
        // feature, so a software adapter (SwiftShader/lavapipe) reports as unsupported here.
        // Otherwise the readiness badge is a false positive and every browser duel fails.
        const descriptor = `${vendor} ${architecture} ${device}`.toLowerCase();
        if (/swiftshader|software|lavapipe|llvmpipe|microsoft basic/.test(descriptor)) {
            return { supported: false, vendor, architecture, device, reason: 'Only a software GPU adapter is available — in-browser models need hardware acceleration.' };
        }
        if (!(adapter.features && adapter.features.has('shader-f16'))) {
            return { supported: false, vendor, architecture, device, reason: 'GPU lacks the shader-f16 feature required to run in-browser models.' };
        }
        try {
            const dev = await adapter.requestDevice({ requiredFeatures: ['shader-f16'] });
            if (!dev) {
                return { supported: false, vendor, architecture, device, reason: 'Could not create a WebGPU device with shader-f16.' };
            }
            if (dev.destroy) dev.destroy();
        } catch (e) {
            return { supported: false, vendor, architecture, device, reason: 'WebGPU device (shader-f16) could not be created: ' + (e.message || '') };
        }
        return { supported: true, vendor, architecture, device, reason: '' };
    } catch (e) {
        return { supported: false, vendor: '', architecture: '', device: '', reason: e.message };
    }
};

// ── WebNN / NPU detection ───────────────────────────────────────────────────
window.checkWebNn = async function () {
    if (!('ml' in navigator)) {
        return { supported: false, deviceType: '', reason: 'navigator.ml not available — WebNN API not supported in this browser.' };
    }
    const order = ['npu', 'gpu', 'cpu'];
    for (const deviceType of order) {
        try {
            const ctx = await navigator.ml.createContext({ deviceType });
            if (ctx) return { supported: true, deviceType, reason: '' };
        } catch (_) { /* device type not available, try next */ }
    }
    return { supported: false, deviceType: '', reason: 'No WebNN device context could be created.' };
};

// ── Model file detection ─────────────────────────────────────────────────────
// Local presence is checked via the server API (a 200 JSON response) rather than a
// static HEAD on mlc-chat-config.json, which logged a 404 in the browser console for
// every not-yet-downloaded model. Only the CDN fallback uses a network probe (the
// HuggingFace HEAD returns 200), so a fresh machine no longer floods the console.
window.checkModelFile = async function (webLlmModelId, cdnBaseUrlTemplates) {
    if (webLlmModelId) {
        try {
            const r = await fetch(`/api/models/download-status/${encodeURIComponent(webLlmModelId)}`,
                { credentials: 'include', cache: 'no-store' });
            if (r.ok) {
                const status = await r.json();
                if (status && status.downloaded) {
                    return { available: true, source: 'local', baseUrl: `${window.location.origin}/models/${webLlmModelId}/` };
                }
            }
        } catch { /* fall through to CDN probe */ }
    }
    // Not present locally → probe the configured CDN templates (skip the noisy local HEAD).
    return window.resolveBrowserModelAvailability(
        webLlmModelId,
        window.location.origin + '/models/',
        cdnBaseUrlTemplates,
        /* skipLocalProbe */ true);
};

// ── Diagnostic runner ────────────────────────────────────────────────────────
window.runModelDiag = async function (dotnetRef, diagId, webLlmModelId, prompt, cdnBaseUrlTemplates) {
    if (_diagWorkers[diagId]) {
        _diagWorkers[diagId].terminate();
        delete _diagWorkers[diagId];
    }

    const worker = new Worker('/js/webllm-worker.js?v=5', { type: 'module' });
    _diagWorkers[diagId] = worker;

    let firstTokenMs = -1;
    let genStartMs   = -1;

    function friendlyError(msg) {
        if (!msg) return 'Worker error.';
        if (msg.includes('importScripts') || msg.includes('Failed to fetch') || msg.includes('ERR_BLOCKED') || msg.includes('failed to load')) {
            return 'WebLLM engine could not load. Check your internet connection and try again.';
        }
        if (msg.includes('out of memory') || msg.includes('OOM') || msg.includes('VRAM')) {
            return 'Ran out of GPU memory. Run models one at a time, or try a smaller model first.';
        }
        if (msg.includes('Device was lost') || msg.includes('mapAsync') || msg.includes('GPUBuffer') || msg.includes('external Instance reference')) {
            return 'GPU ran out of memory — too many models running at once. Run models one at a time, or restart the browser and try a smaller model.';
        }
        if (msg.includes('WebGPU') || msg.includes('requestAdapter')) {
            return 'WebGPU initialisation failed. Ensure your browser supports WebGPU.';
        }
        if (msg.includes('Missing model_lib')) {
            return 'Model library not found — this model ID may not be supported by the local runner.';
        }
        // Strip noisy stack-trace prefix and stack frames
        return msg.replace(/^Uncaught\s+/i, '').replace(/\s+at\s+\S+\s*\([^)]+\)/g, '').trim().slice(0, 200);
    }

    worker.onmessage = function (e) {
        const m = e.data;
        if (m.type === 'status') {
            if (m.status === 'Initializing') {
                const progress = m.loadProgress || 0;
                const detail = (progress === 0 && (!m.detail || /^loading/i.test(m.detail)))
                    ? 'Compiling GPU shaders…'
                    : (m.detail || 'Loading model…');
                dotnetRef.invokeMethodAsync('OnDiagStep', diagId, 'Loading', detail, progress);
            } else if (m.status === 'Generating') {
                if (genStartMs < 0) genStartMs = Date.now();
                if (firstTokenMs < 0 && (m.tokenCount || 0) > 0) {
                    firstTokenMs = Date.now() - genStartMs;
                }
                const tc = m.tokenCount || 0;
                const detail = tc > 0 ? tc + ' tokens — ' + (m.detail || '') : (m.detail || 'Generating...');
                dotnetRef.invokeMethodAsync('OnDiagStep', diagId, 'Generating', detail, 100);
            }
        } else if (m.type === 'complete') {
            const loadMs  = m.warmUpMs || 0;
            const totalMs = m.totalMs  || 0;
            const genMs   = Math.max(1, totalMs - loadMs);
            const tokens  = m.tokenCount || 0;
            const tps     = Math.round((tokens / genMs) * 1000);
            const ftMs    = firstTokenMs >= 0 ? firstTokenMs : genMs;
            dotnetRef.invokeMethodAsync('OnDiagResult', diagId, loadMs, ftMs, tps, tokens, m.htmlOutput || '', null, !!m.cacheHit);
            worker.terminate();
            delete _diagWorkers[diagId];
        } else if (m.type === 'error') {
            dotnetRef.invokeMethodAsync('OnDiagResult', diagId, -1, -1, 0, 0, '', friendlyError(m.reason), false);
            worker.terminate();
            delete _diagWorkers[diagId];
        }
    };

    worker.onerror = function (err) {
        dotnetRef.invokeMethodAsync('OnDiagResult', diagId, -1, -1, 0, 0, '', friendlyError(err.message), false);
        delete _diagWorkers[diagId];
    };

    const availability = await window.resolveBrowserModelAvailability(
        webLlmModelId,
        window.location.origin + '/models/',
        cdnBaseUrlTemplates);

    worker.postMessage({
        modelId: diagId,
        webLlmModelId: webLlmModelId,
        prompt: prompt,
        localModelBaseUrl: availability.baseUrl,
    });
};

// ── Cancel helpers ───────────────────────────────────────────────────────────
window.cancelModelDiag = function (diagId) {
    if (_diagWorkers[diagId]) { _diagWorkers[diagId].terminate(); delete _diagWorkers[diagId]; }
};

window.cancelAllModelDiags = function () {
    Object.keys(_diagWorkers).forEach(function (id) {
        _diagWorkers[id].terminate();
        delete _diagWorkers[id];
    });
};

// ── sessionStorage persistence ───────────────────────────────────────────────
window.saveLabResult = function (modelId, data) {
    try { sessionStorage.setItem('lab_' + modelId, JSON.stringify(data)); } catch (_) {}
};

window.loadLabResults = function () {
    const out = {};
    try {
        for (let i = 0; i < sessionStorage.length; i++) {
            const key = sessionStorage.key(i);
            if (key && key.startsWith('lab_')) {
                out[key.slice(4)] = JSON.parse(sessionStorage.getItem(key));
            }
        }
    } catch (_) {}
    return out;
};

// ── Tested-count pulse animation ─────────────────────────────────────────────
window.pulseTestedCount = function () {
    const el = document.querySelector('.lab__env-stat--tested');
    if (!el) return;
    el.classList.remove('lab__env-stat--pulse');
    void el.offsetWidth;
    el.classList.add('lab__env-stat--pulse');
};
