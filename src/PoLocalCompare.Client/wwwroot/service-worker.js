// No-op service worker. The WebLLM bundle probes for this URL on every page load and logs a
// 404 in the console when it is absent, which is noise without a benefit — this app caches
// nothing offline and the API owns the only network traffic that matters. Returning 200 with
// the smallest valid script silences the probe without enabling any background work.
self.addEventListener('install', () => {
  // Activate immediately; we have nothing to precache.
  self.skipWaiting();
});

self.addEventListener('activate', (event) => {
  // Drop any previously-cached responses that might have been claimed by an earlier build.
  event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', () => {
  // Pass-through: do not intercept any request. The browser's normal network stack handles
  // everything — this script is only here so navigator.serviceWorker.register('/service-worker.js')
  // resolves instead of 404'ing.
});
