// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).
//
// One exception (ADR 0206): the wwwroot/ files of apps installed by hackpkg live in the OS, not on
// the server. The OS copies them into a cache and this worker answers _content/<assembly>/<path>
// from it; every other request goes to the network as before.
self.importScripts('./_content/HackerOs.Infrastructure.Browser/installedAppAssets.sw.js');

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
self.addEventListener('fetch', event => {
    const installedAppAssets = self.hackerOsInstalledAppAssets;
    if (installedAppAssets.handles(event.request)) {
        event.respondWith(installedAppAssets.respond(event.request));
    }
});
