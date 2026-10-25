# GitHub Actions validations

What the workflows in `.github/workflows/` check, when they run, and how to
read a failure. To run the same checks on your own machine, see
[`building-and-testing.md`](building-and-testing.md). Site publication is
detailed in [`github-pages-deployment.md`](github-pages-deployment.md).

## The workflows

| Workflow (file) | Triggers | What it checks |
| --- | --- | --- |
| **Verify HackerOS WASM** (`deploy-wasm.yml`) | Any PR that touches `wasm2/HackerOs/**` (except `wasm2/HackerOs/docs/**`) or this workflow; push to `main` and to `*/base`; manual | Local Windowing/Taskbar packages, restore, Release build (warnings as errors), Razor scan (no inline `<style>`, `<script>` or `on*` handler), vulnerable packages, all non-browser tests, PWA publication. |
| **Verify local Windowing and Taskbar packages** (`window-taskbar-packages.yml`) | PR or push (`main`, `*/base`) that touches the windowing, theme, icon, taskbar projects or the SampleHost example | Packages and symbols produced in dependency order, example restored **only** from these packages, built and published. |
| **Deploy to GitHub Pages** (`deploy-to-pages.yml`) | Push to `main` (deploys); PR that touches the legacy (`src/**`, `package*.json`, `tsconfig*.json`, `webpack.config.js`), `assemble-pages.ps1` or this workflow (builds without deploying); manual | Legacy build for `/legacy/`, WASM publication, assembly of the combined site. A PR has its own concurrency group: it never cancels a `main` deployment. |
| **Verify HackerOS browser tests (manual)** (`verify-browser-tests.yml`) | Manual only (`workflow_dispatch`) | The six Playwright projects, with the Chromium installed by Playwright. |

PRs to `main` as well as to an integration branch `<series>/base` are
verified: the `pull_request` trigger has no branch filter. The push to
`*/base` also verifies the state of the branch after each work item merge.

A PR that only touches docs (`doc/**`, `*.md` at the root) runs no workflow.
The `README.md` files of the projects under `wasm2/HackerOs` remain build
inputs (NuGet packages, `AnalyzerReleases.*.md`): modifying them triggers the
verification.

## Flaky tests and retry

`build/run-test-suite.ps1 -RetryFailedTests N` reruns, after a project
fails, **only its failing tests** (read from the `.trx`), up to `N` times.
The workflows pass `-RetryFailedTests 1`:

- a test that passes on retry does not fail the verification; it appears as a
  "Flaky test" warning on the PR and in the job summary ("Flaky tests");
- a test that fails twice fails the verification;
- a failing project with no failing test in its `.trx` (crash, runner
  timeout) is never rerun and fails the verification.

The results of the first pass stay in
`artifacts/test-results/<suite>/<project>/`, those of the retry in `retry-1/`
next to it; the `hackeros-verification-<attempt>` artifact contains them.

Without the parameter (the default locally), the script keeps its strict
behavior: a failure is a failure. Known flaky tests are listed in
[`pitfalls.md`](pitfalls.md#4-tests-et-e2e); seeing one pass on retry on a PR
is not a regression, but a test that shows up there often deserves to be
stabilized.

## Reading a failure

1. Open the `verify` job and search for `Test projects failed:`: the message
   names the failing projects. The job summary also lists the failures and
   the flaky tests.
2. Download the `hackeros-verification-<attempt>` artifact for the `.trx`
   files.
3. Reproduce locally with the same command
   (`./build/run-test-suite.ps1 -Suite NonBrowser`) after a Release build.

A green local build and tests are still required before merging: the GitHub
verification does not run the Playwright suites.

## Action versions

The official actions are at versions that run on Node 24
(`checkout@v6`, `setup-dotnet@v5`, `setup-node@v6`, `cache@v5`,
`upload-artifact@v6`, `configure-pages@v6`, `upload-pages-artifact@v5`,
`deploy-pages@v5`): the runners force Node 24 and flagged the Node 20
versions as deprecated.

## .NET SDK installation

The four workflows install the SDK through the local action
`.github/actions/setup-dotnet-sdk`. It reads the latest version of the
channel (`latest-sdk` from
`https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`)
and passes it **exactly** to `actions/setup-dotnet`.

Why: with `dotnet-version: 10.0.x`, the install script follows the channel's
`aka.ms` link, which on 2026-10-25 pointed to
`dotnetorigin.blob.core.windows.net`, a storage that answered HTTP 409; the
job failed before any compilation. With an exact version, the script
downloads from `builds.dotnet.microsoft.com`, which answered normally. If the
metadata file is unreadable, the action falls back to `10.0.x` with a
warning. `wasm2/HackerOs/global.json` (`10.0.100`, `latestFeature`) accepts
any newer 10.0 version.

## Required checks

Making a workflow required is configured in GitHub (Settings ▸ Branches ▸
protection rule for `main` or `*/base`), not in the repository. Note: a
path-filtered workflow does not run on a PR that does not touch those paths,
and a required check that never ran blocks the PR in "pending".
