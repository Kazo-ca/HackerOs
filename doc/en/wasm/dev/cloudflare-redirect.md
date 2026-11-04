> [Version française](../../../fr/wasm/dev/cloudflare-redirect.md)

# Cloudflare redirect from hackos.kazo.ca to os.kazo.ca

The Worker script is
[`wasm2/HackerOs/build/cloudflare/hackos-redirect-worker.mjs`](../../../../wasm2/HackerOs/build/cloudflare/hackos-redirect-worker.mjs).
Local check: `node wasm2/HackerOs/build/cloudflare/test-redirect-worker.mjs`.
Context: [ADR 0228](adr/0228-rename-to-kazoos.md) (KazoOS).

## What the Worker does

- `https://hackos.kazo.ca/service-worker.js` (with or without `?mode=installed-apps`)
  answers with a small script that replaces the PWA's cached service worker: it empties
  the OS file cache, unregisters itself and sends every open tab to the same address on
  `os.kazo.ca`. A browser refuses a redirect on the service worker script, which is why a
  plain "Redirect Rule" would leave installed PWAs stuck on their cached copy.
- Every other address gets a permanent redirect (301, or 308 for a POST) to
  `https://os.kazo.ca`, keeping the path and the query:
  `hackos.kazo.ca/legacy/?a=1` → `os.kazo.ca/legacy/?a=1`.
- IndexedDB and localStorage on `hackos.kazo.ca` are not touched.

## Step 0: os.kazo.ca must already serve the OS

GitHub Pages serves one custom domain per repository. Before turning on the redirect:

1. GitHub, repository → **Settings → Pages → Custom domain**: replace `hackos.kazo.ca`
   with `os.kazo.ca`, **Save**, then tick **Enforce HTTPS** when GitHub offers it.
2. In Cloudflare, the `os` record must be a `CNAME` to `kazo-ca.github.io`. Keep it
   **DNS only** (grey cloud) while GitHub issues the certificate.
3. Open `https://os.kazo.ca/`: the OS must start. Otherwise do not do step 2: browsers
   keep a 301 in cache.

## Step 1: create the Worker

1. Cloudflare dashboard → **Workers & Pages** (left menu, under Compute) → **Create** →
   **Create Worker** ("Hello World" template).
2. Name: `hackos-redirect` → **Deploy**.
3. **Edit code**: replace the whole content with `hackos-redirect-worker.mjs` → **Deploy**.

## Step 2: attach the Worker to hackos.kazo.ca

1. In the `hackos-redirect` Worker → **Settings** → **Domains & Routes** → **Add** →
   **Custom domain** → `hackos.kazo.ca` → **Add domain**.
2. If Cloudflare refuses because a `hackos` DNS record already exists: go to
   **kazo.ca → DNS → Records**, delete the `hackos` record (the CNAME to GitHub), then
   redo point 1. Cloudflare creates the record and the certificate itself.

(Alternative that keeps the DNS record: set `hackos` to **Proxied** (orange cloud), then
in the Worker **Add → Route**: `hackos.kazo.ca/*`, zone `kazo.ca`.)

## Step 3: check

- `https://hackos.kazo.ca/` and `https://hackos.kazo.ca/legacy/` land on `os.kazo.ca`.
- `https://hackos.kazo.ca/service-worker.js` shows the small script (no redirect).
- A PWA installed from hackos.kazo.ca: on its next opening it still loads its cached copy,
  sees the new service worker, then leaves for `os.kazo.ca`. The PWA has to be installed
  again from `os.kazo.ca`.

## Cost

Free: the Workers Free plan gives 100,000 requests per day.
