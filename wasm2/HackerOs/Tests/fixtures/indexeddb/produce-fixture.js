// Produces an IndexedDB upgrade fixture (ADR 0099 section 4) by driving an OLD HackerOS build
// through its real UI -- first-run administrator, one uploaded file, one appearance setting --
// then dumping every object store of the "hackeros" database (key path, auto-increment, indexes,
// records) to JSON. Tooling only: never shipped, never referenced by a host.
//
// usage: node produce-fixture.js <url> <schemaVersion> <commit> <out.json> <screenshotPrefix>
// env:   PLAYWRIGHT_MODULE  path of the node "playwright" package (default: "playwright")
//        CHROMIUM           Chromium executable (default: Playwright's own)
// Full procedure: doc/wasm/dev/building-and-testing.md, "Fixtures de montée de version IndexedDB".
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const fs = require('fs');

const [url, versionArg, commit, outJson, shot] = process.argv.slice(2);
const version = Number(versionArg);
const admin = { loginName: 'admin', displayName: 'Fixture Admin', password: `Fixture-v${version}-pass` };
const file = {
  directory: '/home/admin/Documents',
  name: 'upgrade-note.txt',
  content: `Written by HackerOS at IndexedDB schema v${version} (${commit}).`
};
const accent = 'cyan';

async function step(page, name, fn) {
  console.log('step:', name);
  try { await fn(); } catch (e) { await page.screenshot({ path: `${shot}-fail-${name}.png` }); throw e; }
}

(async () => {
  const browser = await chromium.launch(process.env.CHROMIUM ? { executablePath: process.env.CHROMIUM } : {});
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  page.on('pageerror', e => console.log('pageerror', e.message));
  page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') console.log('console:', m.type(), m.text().slice(0, 400)); });
  page.setDefaultTimeout(30000);
  await page.goto(url);

  await step(page, 'onboarding', async () => {
    await page.getByLabel('Login name').waitFor({ timeout: 60000 });
    await page.getByLabel('Login name').fill(admin.loginName);
    await page.getByLabel('Display name').fill(admin.displayName);
    await page.getByLabel('Password', { exact: true }).fill(admin.password);
    await page.getByLabel('Confirm password').fill(admin.password);
    await page.getByRole('button', { name: 'Create local profile' }).click();
    await page.getByRole('button', { name: 'App launcher' }).waitFor({ timeout: 60000 });
  });

  const openApp = async (name) => {
    await page.getByRole('button', { name: 'App launcher' }).click();
    await page.getByRole('textbox', { name: 'Search applications' }).fill(name);
    const option = page.getByRole('option', { name: new RegExp('^' + name) });
    await option.first().click();
  };

  await step(page, 'write-file', async () => {
    // File Explorer's upload is the "write a file" path that works on every old build: the
    // Text Editor of 7a20401 fails to save a brand-new file (expectedRevision 0 rejected).
    await openApp('File Explorer');
    const addressInput = page.getByLabel('Current path address');
    await addressInput.waitFor();
    await addressInput.fill(file.directory);
    await addressInput.press('Enter');
    await page.locator('tr.entry-row').first().waitFor({ state: 'attached', timeout: 3000 }).catch(() => {});
    await page.waitForTimeout(1000);
    await page.locator('#fileInput').setInputFiles({ name: file.name, mimeType: 'text/plain', buffer: Buffer.from(file.content, 'utf8') });
    await page.locator('tr.entry-row', { hasText: file.name }).waitFor({ timeout: 30000 });
  });

  await step(page, 'setting', async () => {
    await openApp('Settings');
    await page.locator('.accent-btn.accent-cyan').click();
    await page.waitForTimeout(2000);
  });

  await page.waitForTimeout(2000);
  await page.screenshot({ path: `${shot}-desktop.png` });

  const dump = await page.evaluate(async () => {
    const open = () => new Promise((res, rej) => { const r = indexedDB.open('hackeros'); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
    const db = await open();
    const stores = [];
    for (const name of Array.from(db.objectStoreNames)) {
      const tx = db.transaction(name, 'readonly');
      const store = tx.objectStore(name);
      const records = await new Promise((res, rej) => { const r = store.getAll(); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
      const keys = await new Promise((res, rej) => { const r = store.getAllKeys(); r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
      const indexes = Array.from(store.indexNames).map(i => { const ix = store.index(i); return { name: ix.name, keyPath: ix.keyPath, unique: ix.unique, multiEntry: ix.multiEntry }; });
      // Typed arrays/ArrayBuffers are not JSON: encode them as base64 with a type marker.
      const encode = (v) => {
        if (v instanceof ArrayBuffer) v = new Uint8Array(v);
        if (ArrayBuffer.isView(v)) { let s = ''; const b = new Uint8Array(v.buffer, v.byteOffset, v.byteLength); for (const x of b) s += String.fromCharCode(x); return { $type: v.constructor.name === 'ArrayBuffer' ? 'ArrayBuffer' : v.constructor.name, base64: btoa(s) }; }
        if (v instanceof Date) return { $type: 'Date', value: v.toISOString() };
        if (Array.isArray(v)) return v.map(encode);
        if (v && typeof v === 'object') { const o = {}; for (const k of Object.keys(v)) o[k] = encode(v[k]); return o; }
        return v;
      };
      stores.push({ name, keyPath: store.keyPath, autoIncrement: store.autoIncrement, indexes, records: records.map(encode), keys: store.keyPath === null ? keys.map(encode) : undefined });
    }
    const version = db.version; db.close();
    return { version, stores };
  });

  const fixture = {
    $comment: 'IndexedDB fixture produced by booting an old HackerOS build; see doc/wasm/dev/building-and-testing.md.',
    databaseName: 'hackeros',
    version: dump.version,
    producedFrom: { commit, method: 'booted', host: 'test/test (Release)', producedOn: new Date().toISOString().slice(0, 10) },
    expectations: { admin, file: { path: `${file.directory}/${file.name}`, content: file.content }, setting: { document: 'appearance', key: 'accent', value: accent } },
    objectStores: dump.stores
  };
  if (dump.version !== version) throw new Error(`Expected DB version ${version}, got ${dump.version}`);
  fs.writeFileSync(outJson, JSON.stringify(fixture, null, 2) + '\n');
  console.log('wrote', outJson, dump.stores.map(s => `${s.name}:${s.records.length}`).join(' '));
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
