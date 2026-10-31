> [Version française](../../../fr/wasm/dev/github-pages-deployment.md)

# GitHub Pages publication: WASM and legacy

## Purpose

The public repository is [Kazo-ca/HackerOs](https://github.com/Kazo-ca/HackerOs).
The standalone WebAssembly version **2.2.0** is published at the root of
`https://hackos.kazo.ca/`; the original TypeScript version is kept under
`https://hackos.kazo.ca/legacy/`. See [ADR 0167](adr/0167-combined-pages-deployment.md).

## Workflow and independent builds

`.github/workflows/deploy-to-pages.yml` is the only workflow that deploys Pages.
It runs on `main` when the relevant sources, the build files or this workflow
change, and can be launched manually.

| Output | Inputs of its cache key | Build if the key does not exist |
| --- | --- | --- |
| `dist/` (legacy) | `src/**`, `package*.json`, `tsconfig*.json`, `webpack.config.js` | Node 20, `npm ci`, `npm run build:prod -- --output-public-path /legacy/` |
| `wasm2/HackerOs/artifacts/pages-publish/wwwroot/` | `wasm2/HackerOs/**`, excluding `bin`, `obj`, `artifacts`, `node_modules`, `TestResults` | .NET 10, `wasm-tools` workload, `dotnet publish` of `OS/HackerOs.Ecosystem` in Release |

The keys contain the runner OS and the content fingerprint. An exact cache
allows reusing the published bytes and skipping the whole relevant build
chain. There is no prefix restore that could accept an old version. A legacy
change therefore does not rebuild the WASM, and vice versa. The first
publication and an expired, deleted or evicted cache rebuild the missing
output, even without a new modification of that part. Build files shared with
the legacy also count as changes of that version. Changing the `v1` prefix of
a key deliberately invalidates its cache.

`deploy-wasm.yml` keeps the compilation, the Razor scan, the dependency audit,
the non-browser tests and the downloadable WASM artifact. It does not deploy
Pages and does not request the Pages/OIDC permissions. Its triggers are
limited to the WASM and its own workflow.

On a pull request that touches the legacy, `assemble-pages.ps1` or this
workflow, `deploy-to-pages.yml` builds the combined site without uploading or
deploying it, in its own concurrency group; it therefore never cancels a
`main` deployment. Overview: [`ci-workflows.md`](ci-workflows.md).

Playwright is run neither by Pages nor by the automatic checks on `main`. The
six browser test projects remain available in
`.github/workflows/verify-browser-tests.yml`, launched every night on `main`
when `main` changed, or by **Actions → Verify HackerOS browser tests → Run
workflow** ([`ci-workflows.md`](ci-workflows.md#browser-tests-every-night)). This
workflow keeps its diagnostics and performs no deployment. See
[the test suites](building-and-testing.md#testing) for the local commands.

### Test separation tasks

- [x] Pages publication independent of Playwright.
- [x] Automatic verification of the 41 non-browser projects.
- [x] Six Playwright projects kept in a dedicated workflow (every night on `main` and manual).
- [x] Project selection verified with `run-test-suite.ps1 -ListOnly`.

## Assembly and routing

`build/assemble-pages.ps1` copies the WASM publication unmodified into
`artifacts/pages/`, then the legacy bundle into `artifacts/pages/legacy/`. It
adds `.nojekyll` and the `CNAME` `hackos.kazo.ca`. The destination folder
must be empty; a missing required entry aborts the publication.

The HTML base of the WASM remains `/`. The legacy bundle uses the Webpack
public path `/legacy/`, including for its chunks and assets. The LESS source
keeps Google Fonts as a browser CSS import, without downloading the font
during compilation.

The PWA integrity manifest is not rewritten and does not contain the legacy.
The WASM service worker leaves same-origin `/legacy` and `/legacy/**`
requests to the network: no substitution by the WASM shell, no addition of
its isolation headers to the legacy. Other paths keep the existing offline
strategy.

## Visible information

- Login and first account creation: build version, "Author: Benoit Robin" (link to
  https://kazo.ca) and "GitHub project: [icon] Kazo-ca/HackerOs" (link to the
  repository), labels in the system language
  ([ADR 0187](adr/0187-guest-start-and-sign-in-credits.md)). If the host
  name is exactly `hackos.kazo.ca`, a message indicates the move of the
  original to `https://hackos.kazo.ca/legacy`. Ports do not affect this test;
  look-alike subdomains and URL parameters do not enable it.
- WASM recovery and fatal errors: same credits and the same version.
- Settings, About: current version through `HackerOsSdk.Version`, author (kazo.ca
  link) and GitHub project in the same format.
- Legacy Settings, About: GitHub URL visible, legacy version unchanged.
- Package metadata also points to the new repository. The common version is
  defined once in `build/Versions.props` (`2.2.0`).

## Local verification

From the repository root, on an output folder that is still empty:

```powershell
npm ci
npm run build:prod -- --output-public-path /legacy/
dotnet publish wasm2/HackerOs/OS/HackerOs.Ecosystem/HackerOs.Ecosystem.csproj -c Release -m:1 -o wasm2/HackerOs/artifacts/pages-publish
./wasm2/HackerOs/build/assemble-pages.ps1

$env:HACKEROS_PAGES_ROOT = (Resolve-Path wasm2/HackerOs/artifacts/pages).Path
dotnet test wasm2/HackerOs/Tests/HackerOs.Pwa.E2E.Tests/HackerOs.Pwa.E2E.Tests.csproj --filter FullyQualifiedName~PublishedPagesTests
```

The test serves the static artifact, enables the real service worker, creates
an account, checks About, opens the legacy and its About screen, then reloads
it. Without `HACKEROS_PAGES_ROOT`, this specific test is ignored during
ordinary test runs. `ProjectInformationTests` covers the allowed and refused
domains, and `ResourceParityTests` verifies the English/French translations.

## Tasks

- [x] Keep a single publisher of the Pages artifact.
- [x] Separate the build keys and conditions of the two versions.
- [x] Publish WASM at the root and legacy under `/legacy/`.
- [x] Update version and repository references.
- [x] Limit the move message to the requested domain.
- [x] Finish the browser validation on the combined artifact.

Validation of 2026-10-24: Release WASM publication succeeded, legacy build
succeeded (existing bundle size warnings), 103 targeted product
information/translation tests passed and the real `PublishedPagesTests` flow
passed after enabling the service worker. The workflows were parsed as YAML;
the independent conditions, the uniqueness of the deployment and the
preservation of the PWA manifest bytes were checked locally. The first remote
deployment is triggered by the next push to `main`, or manually on GitHub.
