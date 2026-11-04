// Checks hackos-redirect-worker.mjs without Cloudflare (Node 18+):
//   node wasm2/HackerOs/build/cloudflare/test-redirect-worker.mjs
// See doc/en/wasm/dev/cloudflare-redirect.md.
import assert from 'node:assert/strict';
import worker from './hackos-redirect-worker.mjs';

async function check(url, init, expectedStatus, expectedLocation) {
  const response = await worker.fetch(new Request(url, init));
  assert.equal(response.status, expectedStatus, url);
  assert.equal(response.headers.get('location'), expectedLocation, url);
  return response;
}

await check('https://hackos.kazo.ca/', undefined, 301, 'https://os.kazo.ca/');
await check('https://hackos.kazo.ca/legacy/?a=1', undefined, 301, 'https://os.kazo.ca/legacy/?a=1');
await check('https://hackos.kazo.ca/api/x', { method: 'POST', body: 'x' }, 308, 'https://os.kazo.ca/api/x');

for (const url of ['https://hackos.kazo.ca/service-worker.js', 'https://hackos.kazo.ca/service-worker.js?mode=installed-apps']) {
  const response = await check(url, undefined, 200, null);
  assert.match(response.headers.get('content-type'), /javascript/);
  assert.match(response.headers.get('cache-control'), /no-store/);
  const script = await response.text();
  new Function(script); // parses
  assert.match(script, /unregister\(\)/);
  assert.match(script, /os\.kazo\.ca/);
}

console.log('hackos-redirect-worker: all checks passed');
