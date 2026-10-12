/**
 * weights.js — what WebLLM has cached in THIS browser, and a way to delete it.
 *
 * WebLLM stores weights with the Cache API under fixed scope names; nothing in the app showed
 * them or could free them, so the only way to reclaim several GB was DevTools. Imported on
 * demand by Catalog.razor. The 6.5 MB web-llm.js bundle has its own delete helper, but loading
 * it on the main thread for this would cost more than the dozen lines below.
 *
 * The compiled .wasm libraries (scope "webllm/wasm") are left alone: they are a few MB, and one
 * library is shared by every quantisation of a model.
 */
const SCOPES = ['webllm/model', 'webllm/config'];

/** The model a cached URL belongs to: /models/<id>/… when vendored, /<owner>/<id>/resolve/… on the Hub. */
function modelIdOf(url) {
    const path = new URL(url).pathname;
    const match = path.match(/\/models\/([^/]+)\//) ?? path.match(/^\/[^/]+\/([^/]+)\/resolve\//);
    return match ? decodeURIComponent(match[1]) : null;
}

async function eachEntry(visit) {
    if (!('caches' in globalThis)) return;
    for (const scope of SCOPES) {
        if (!(await caches.has(scope))) continue;
        const cache = await caches.open(scope);
        for (const request of await cache.keys()) {
            const id = modelIdOf(request.url);
            if (id) await visit(cache, request, id);
        }
    }
}

/** { models: [{ id, mb }] largest first, usageMb, quotaMb } — the last two null where unsupported. */
export async function list() {
    const bytes = new Map();
    await eachEntry(async (cache, request, id) => {
        const response = await cache.match(request);
        // The header when present; a cached Blob is disk-backed, so asking its size reads nothing.
        const size = Number(response?.headers.get('content-length')) || (await response?.blob())?.size || 0;
        bytes.set(id, (bytes.get(id) ?? 0) + size);
    });

    const estimate = await navigator.storage?.estimate?.();
    const toMb = (value) => (typeof value === 'number' ? value / 1048576 : null);
    return {
        models: [...bytes].map(([id, size]) => ({ id, mb: size / 1048576 })).sort((a, b) => b.mb - a.mb),
        usageMb: toMb(estimate?.usage),
        quotaMb: toMb(estimate?.quota),
    };
}

/** Deletes one model's cached weights and config. It downloads again the next time it runs. */
export async function remove(modelId) {
    await eachEntry(async (cache, request, id) => {
        if (id === modelId) await cache.delete(request);
    });
}
