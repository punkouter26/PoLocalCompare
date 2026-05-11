/**
 * T051 — WebLLM JS interop glue
 * Bridging between Blazor WebLlmService.cs and the webllm-worker.js Web Worker.
 * Supports concurrent dual-model inference (e.g. two local models in a duel).
 */

// Map of modelId → Worker, allowing concurrent local model inference
const workers = {};

/**
 * Called from Blazor WebLlmService via IJSRuntime.InvokeVoidAsync("startWebLlmInference", dotnetRef, modelId, webLlmModelId, prompt)
 */
window.startWebLlmInference = function (dotnetRef, modelId, webLlmModelId, prompt) {
    // Terminate any previous worker for this modelId
    if (workers[modelId]) {
        workers[modelId].terminate();
        delete workers[modelId];
    }

    const worker = new Worker('/js/webllm-worker.js');
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
    const localModelBaseUrl = window.location.origin + '/models/';
    worker.postMessage({ modelId, webLlmModelId, prompt, localModelBaseUrl });
};
