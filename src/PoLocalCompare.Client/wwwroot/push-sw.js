// No-op service worker.
//
// Some browsers (Chromium-based) issue a speculative fetch for /push-sw.js on every page
// load, even when the page has not requested push permissions. Without this file the server
// logged a 404 every page load; the file does nothing but be there.
//
// This app does not implement push notifications. If we ever do, this file is the place to
// register the PushManager subscription and the `push` event handler.
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (event) => event.waitUntil(self.clients.claim()));
