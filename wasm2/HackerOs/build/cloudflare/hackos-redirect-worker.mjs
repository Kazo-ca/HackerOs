/**
 * Cloudflare Worker: moves hackos.kazo.ca to os.kazo.ca (KazoOS, ADR 0228).
 *
 * - /service-worker.js (with or without a query string, such as
 *   ?mode=installed-apps) answers 200 with a script that replaces the cached
 *   PWA worker: it deletes the app shell caches, unregisters itself and sends
 *   every open tab to the same path on os.kazo.ca. A browser refuses a
 *   redirect on a service worker script, so a plain redirect rule would leave
 *   installed PWAs stuck on their cached copy.
 * - Every other request gets a permanent redirect to os.kazo.ca, keeping the
 *   path and the query string (301 for GET/HEAD, 308 for other methods so
 *   the method and body are kept).
 *
 * IndexedDB and localStorage on hackos.kazo.ca are left untouched.
 *
 * Pasted by hand into the Cloudflare dashboard; see
 * doc/en/wasm/dev/cloudflare-redirect.md. Check: node build/cloudflare/test-redirect-worker.mjs
 */
const NEW_HOST = 'os.kazo.ca';

const RETIRE_WORKER = `
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    try {
      const keys = await caches.keys();
      await Promise.all(keys.map(key => caches.delete(key)));
    } catch (e) { /* nothing cached */ }
    await self.clients.claim();
    await self.registration.unregister();
    const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
    for (const client of windows) {
      try {
        const target = new URL(client.url);
        target.host = '${NEW_HOST}';
        target.protocol = 'https:';
        await client.navigate(target.href);
      } catch (e) { /* the page's own controllerchange reload reaches the redirect */ }
    }
  })());
});
`;

export default {
  async fetch(request) {
    const url = new URL(request.url);

    if (url.pathname === '/service-worker.js') {
      return new Response(RETIRE_WORKER, {
        status: 200,
        headers: {
          'Content-Type': 'text/javascript; charset=utf-8',
          'Cache-Control': 'no-store, max-age=0'
        }
      });
    }

    url.host = NEW_HOST;
    url.protocol = 'https:';
    url.port = '';
    const status = request.method === 'GET' || request.method === 'HEAD' ? 301 : 308;
    return Response.redirect(url.toString(), status);
  }
};
