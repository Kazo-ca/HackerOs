> [Version française](../../../fr/wasm/dev/ci-workflows.md)

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
| **Verify HackerOS browser tests** (`verify-browser-tests.yml`) | Every night on `main` (06:17 UTC), **only if `main` changed since the last tested commit**; manual (always runs) | The six Playwright projects, with the Chromium installed by Playwright. A failed night opens or updates an issue ([dedicated section](#browser-tests-every-night)). |
| **Release HackerOS** (`release.yml`) | Successful end of *Verify HackerOS WASM* after a push to `main`; manual (`republish` option) | Publishes a version whose `v<version>` tag does not exist yet: `HackerOs.*` packages, zipped WASM site, push to nuget.org, tag and GitHub Release ([`releases.md`](releases.md), ADR 0173). |
| **Change list entry** (`changelog-entry.yml`) | Any PR to `main` (opened, pushed, label added or removed) | A PR that touches the product (`wasm2/HackerOs` outside `Tests/`, `docs/`, `changes/` and `.md` files) adds at least one entry `wasm2/HackerOs/changes/unreleased/<guid>.json`, unless it has the `no-changelog` label ([`change-list.md`](change-list.md), ADR 0186). The format of the entries is checked by `ChangeEntriesTests` in *Verify HackerOS WASM*. |

PRs to `main` as well as to an integration branch `<series>/base` are
verified: the `pull_request` trigger has no branch filter. The push to
`*/base` also verifies the state of the branch after each work item merge.

A PR that only touches docs (`doc/**`, `*.md` at the root) only runs
*Change list entry* (to `main`), which passes at once since it does not touch the product.
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
[`pitfalls.md`](pitfalls.md#4-tests-and-e2e); seeing one pass on retry on a PR
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
verification of PRs does not run the Playwright suites (they run at night on
`main`, see below).

## Browser tests every night

Benoit's decision (2026-10-30, issue #292): the six Playwright projects run
**every night on `main`**, not on every PR, because known unstable browser
tests ([`known-test-failures.md`](../memory/known-test-failures.md)) would block
PRs. The `verify-browser-tests.yml` workflow has three jobs:

1. **`changes`**: a scheduled run looks for the last run of this workflow on
   `main` that finished its tests (success or failure; a cancelled or
   timed-out run does not count) and compares its commit with `main`'s. If they
   are equal, `main` has not changed: the tests are skipped and the job summary
   says so. A manual run always tests.
2. **`browser-tests`**: Release build, Playwright's Chromium, then
   `run-test-suite.ps1 -Suite Browser -RetryFailedTests 1` (a test that passes
   on the second attempt is reported as "Flaky test", see above). The
   `hackeros-browser-tests-<attempt>` artifact keeps the `.trx` files and the
   traces for 14 days.
3. **`report`** (only on `main`): a failure opens the issue "Les tests
   navigateur de la nuit échouent sur main", or adds a comment to it when it is
   already open (run link and commit). The next green run closes it with a
   comment.

A failure is therefore visible three times: the red workflow status, the open
issue (with its notification) and the `main` commit concerned. If `main` does
not move after a failure, the next night does not rerun the tests: the issue
stays open until the next commit tested green. To retest without a commit, run
the workflow by hand on `main` (Actions ▸ Verify HackerOS browser tests ▸ Run
workflow).

GitHub only starts a `schedule` from the default branch: the nightly trigger
counts from its arrival in `main`.

## Action versions

The official actions are at versions that run on Node 24
(`checkout@v6`, `setup-dotnet@v5`, `setup-node@v6`, `cache@v5`,
`upload-artifact@v6`, `configure-pages@v6`, `upload-pages-artifact@v5`,
`deploy-pages@v5`): the runners force Node 24 and flagged the Node 20
versions as deprecated.

## .NET SDK installation

The five workflows install the SDK through the local action
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
