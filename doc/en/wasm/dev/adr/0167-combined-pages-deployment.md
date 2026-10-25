# ADR 0167: combined Pages publication and independent builds

## Status

Accepted on 2026-10-24 at Benoit's request. Completes the PWA publication and
restricts the service worker interception of ADR 0131 for the legacy sub-site.

## Decision

- Public repository: `https://github.com/Kazo-ca/HackerOs`.
- WASM release: `2.0.3`, through the existing single version of ADR 0098.
- The Pages workflow publishes a single artifact: standalone PWA at `/`,
  old TypeScript bundle at `/legacy/`, domain `hackos.kazo.ca`.
- Two exact caches, computed on the respective inputs, make it possible to rebuild
  only the modified version. No prefix cache; a missing or evicted output
  is rebuilt so that the artifact stays complete.
- The separate WASM workflow continues validation and publication of a
  downloadable artifact, without a second concurrent Pages deployment.
- The assembly modifies no byte signed by the PWA manifest. The worker
  leaves `/legacy` and `/legacy/**` to the network, outside fallback and WASM isolation.
- The product information screens display the repository. The login and
  account creation screens announce the legacy site only on the exact host name
  `hackos.kazo.ca`, with English/French texts.
- The legacy changes authorized here remain limited to its identity,
  its publication build and the startup fixes needed to keep it
  under the new path.

## Usage and verification

See [github-pages-deployment.md](../github-pages-deployment.md): commands,
cache conditions, assembly script and tests of the real publication.
