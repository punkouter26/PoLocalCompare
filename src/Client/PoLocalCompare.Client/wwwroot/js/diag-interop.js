/**
 * diag-interop.js — JS bridge for LocalModelLab.razor <-> webllm-worker.js
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
            const info = await adapter.requestAdapterInfo();
            vendor = info.vendor || '';
            architecture = info.architecture || '';
            device = info.device || '';
        } catch (_) { /* requestAdapterInfo not available in all browsers */ }
        return { supported: true, vendor, architecture, device, reason: '' };
    } catch (e) {
        return { supported: false, vendor: '', architecture: '', device: '', reason: e.message };
    }
};

// ── Model file detection ─────────────────────────────────────────────────────
window.checkModelFile = async function (webLlmModelId) {
    try {
        const r = await fetch(`/models/${webLlmModelId}/mlc-chat-config.json`, { method: 'HEAD' });
        return r.ok;
    } catch {
        return false;
    }
};

// ── Diagnostic runner ────────────────────────────────────────────────────────
window.runModelDiag = function (dotnetRef, diagId, webLlmModelId, prompt) {
    if (_diagWorkers[diagId]) {
        _diagWorkers[diagId].terminate();
        delete _diagWorkers[diagId];
    }

    const worker = new Worker('/js/webllm-worker.js');
    _diagWorkers[diagId] = worker;

    let firstTokenMs = -1;
    let genStartMs   = -1;

    function friendlyError(msg) {
        if (!msg) return 'Worker error.';
        if (msg.includes('importScripts') || msg.includes('Failed to fetch') || msg.includes('ERR_BLOCKED') || msg.includes('failed to load')) {
            return 'WebLLM engine could not load. Check your internet connection and try again.';
        }
        if (msg.includes('out of memory') || msg.includes('OOM') || msg.includes('VRAM')) {
            return 'Ran out of GPU memory. Try a smaller model first.';
        }
        if (msg.includes('WebGPU') || msg.includes('requestAdapter')) {
            return 'WebGPU initialisation failed. Ensure your browser supports WebGPU.';
        }
        // Strip noisy stack-trace prefix
        return msg.replace(/^Uncaught\s+/i, '').slice(0, 180);
    }

    worker.onmessage = function (e) {
        const m = e.data;
        if (m.type === 'status') {
            if (m.status === 'Initializing') {
                dotnetRef.invokeMethodAsync('OnDiagStep', diagId, 'Loading',
                    m.detail || 'Loading model...', m.loadProgress || 0);
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
            dotnetRef.invokeMethodAsync('OnDiagResult', diagId, loadMs, ftMs, tps, tokens, m.htmlOutput || '', null);
            worker.terminate();
            delete _diagWorkers[diagId];
        } else if (m.type === 'error') {
            dotnetRef.invokeMethodAsync('OnDiagResult', diagId, -1, -1, 0, 0, '', friendlyError(m.reason));
            worker.terminate();
            delete _diagWorkers[diagId];
        }
    };

    worker.onerror = function (err) {
        dotnetRef.invokeMethodAsync('OnDiagResult', diagId, -1, -1, 0, 0, '', friendlyError(err.message));
        delete _diagWorkers[diagId];
    };

    worker.postMessage({
        modelId: diagId,
        webLlmModelId: webLlmModelId,
        prompt: prompt,
        localModelBaseUrl: window.location.origin + '/models/',
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
