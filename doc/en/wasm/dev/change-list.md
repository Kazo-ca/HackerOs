> [Version française](../../../fr/wasm/dev/change-list.md)

# Change list: writing an entry

Every PR that changes what a user or an app author sees adds an entry to the change list
([ADR 0186](adr/0186-whats-new-change-list.md)). The list feeds the OS What's new page, the
GitHub Release notes, the `PackageReleaseNotes` of the NuGet packages and `CHANGELOG.md`.

## Where

```
wasm2/HackerOs/changes/
├── unreleased/<guid>.json   one file per change not released yet
└── <version>.json           the entries of a released version (e.g. 2.1.0.json)
```

## Adding an entry

1. Create a lowercase GUID: `python3 -c "import uuid; print(uuid.uuid4())"`,
   `[guid]::NewGuid()` (PowerShell) or `uuidgen | tr A-Z a-z`.
2. Create `wasm2/HackerOs/changes/unreleased/<guid>.json`:

```json
{
  "id": "<guid>",
  "category": "new",
  "audience": ["user"],
  "title": { "en": "Short English title", "fr": "Titre court en français" },
  "description": { "en": "One or two sentences.", "fr": "Une ou deux phrases." },
  "pr": 123,
  "links": ["doc/en/wasm/dev/adr/0186-whats-new-change-list.md"]
}
```

| Field | Required | Values |
| --- | --- | --- |
| `id` | yes | The GUID, same as the file name. |
| `category` | yes | `new`, `improved`, `fixed`, `developer`. |
| `audience` | yes | `["user"]`, `["developer"]` or `["user", "developer"]`. A `developer` entry has `["developer"]` only. |
| `title` | yes | `en` and `fr`, not empty. |
| `description` | no | `en` and `fr`, not empty. |
| `pr` | no | PR number (can be added after the PR is opened). |
| `links` | no | `https` URL or path of a file of the repository from its root. |

3. Run the test: `dotnet test Tests/HackerOs.Ecosystem.Tests --filter ChangeEntriesTests`.

## Writing well

- Write for the person reading the What's new page or the release notes, not for the code
  reviewer: what they can now do, what works better.
- A short title, a one or two sentence description.
- One entry per visible change. A series may have several.
- Changes to the SDK or to the app build tools go into a `developer` entry.

## When not to write one

A PR that changes nothing visible (refactoring, tests, CI, internal build) gets the
**`no-changelog`** label. A PR that only touches `doc/`, `Tests/`, `docs/`, `changes/` or `.md`
files does not need one.

## The PR check

The **Change list entry** workflow (`.github/workflows/changelog-entry.yml`) runs on every PR to
`main` and calls `wasm2/HackerOs/build/tools/check-change-entry.sh <base> <head>`. It fails when
the PR touches `wasm2/HackerOs` (outside `Tests/`, `docs/`, `changes/` and `.md`) without adding a
file in `changes/unreleased/` (or a new `changes/<version>.json`, see below), unless it has the
`no-changelog` label. To run it before pushing:

```bash
wasm2/HackerOs/build/tools/check-change-entry.sh origin/main HEAD
```

In a series, each work item PR to `<series>/base` adds its entry: the final PR to `main` brings
them all.

## In the OS: `/whats-new.json`

When `OS/HackerOs.Ecosystem` builds, the `Tools/HackerOs.Tools.ChangeList` tool reads `changes/`
and writes `obj/…/hackeros-whats-new/whats-new.json`, served at the root of the site like
`/version.json` (ADR 0186 §4.1). It keeps only the entries whose audience contains `user`,
grouped by version, the most recent first; the entries of `unreleased/` make the first group
(`"version": null`, `"since": "<HackerOsVersion>"`). In a group, entries are sorted by category
(`new`, `improved`, `fixed`, `developer`), then from the most recent PR to the oldest. The paths
of `links` become GitHub URLs and `pr` also gives `prUrl`.

To see the file without building the OS:

```bash
dotnet run --project wasm2/HackerOs/Tools/HackerOs.Tools.ChangeList -- \
  site --changes wasm2/HackerOs/changes --version 2.1.0 --output /tmp/whats-new.json
```

In the OS:

- `WhatsNewService` (Platform.Core, `HackerOs.Platform.Core.WhatsNew`) reads the file once per
  page (a new deployment reloads the page) and keeps, for each account, the IDs already seen in
  `~/.local/state/hackeros/whats-new.json`: `{ "seen": [ "<id>", … ] }`. An ID that leaves the
  list stays in the file: an entry never becomes new again.
- **New account** (ADR 0186 §5): when that file does not exist yet (or cannot be read), everything
  is marked seen except the entries of the latest published version and those of the next version
  already deployed, and the file is written at once. With no served list (offline), nothing is
  written: the rule waits for a list.
- Apps read the list through the SDK, with no capability: `HackWhatsNew.GetAsync()` (texts in the
  app's language, English as fallback), `MarkSeenAsync(ids)` and `WatchAsync` (see
  [`sdk-reference.md`](../framework/sdk-reference.md)).

What the user sees (ADR 0186 §4.4 and §4.5):

- **Settings ▸ What's new** (`--page=whats-new`, also through the **What's new?** button of
  About): one group per version, the next one first ("Since 2.1.0 (next version)"), a category
  label and a **New** badge on what was not seen, and "Learn more" and "Pull request" links. The
  badges stay during the visit; the entries become seen when the page closes (another page
  chosen or Settings closed).
- **Notification after sign-in**, once per account and page load, when unseen entries remain:
  "3 new changes since your last visit", with a **View** button that opens the page. It is in the
  clock panel. A notification button can now pass arguments to the app:
  `NotificationAction.LaunchApp(appId, label, arguments)`.
- **Unread notifications dot** next to the clock (desktop) and on the notification shade handle
  (mobile), with the count in the tooltip; opening the panel marks them read
  ([ADR 0196](adr/0196-unread-notifications-dot.md), which replaces the Start button dot). When
  every entry is seen, the what's new notification closes by itself.

## When a version is released

In the PR that changes `build/Versions.props`, run:

```bash
wasm2/HackerOs/build/tools/collect-changes.sh            # today's date (UTC)
wasm2/HackerOs/build/tools/collect-changes.sh --date 2026-11-02
```

The script (tool `Tools/HackerOs.Tools.ChangeList`, `collect` command):

1. moves every entry of `unreleased/` into `changes/<version>.json`, the version being the one of
   `Versions.props`, without changing their content or IDs (**New** badges already seen stay
   seen), then deletes the files of `unreleased/`;
2. regenerates `CHANGELOG.md` (English) and `CHANGELOG-fr.md` (French) at the root of the
   repository.

It refuses to run when `changes/<version>.json` already exists or `unreleased/` is empty.
`--changelog-only` only regenerates the two changelogs. The PR check accepts a new
`changes/<version>.json` as an entry.

Then, on the merge into `main`, with nothing more to do:

- **GitHub Release**: `release.yml` writes the text with the `notes --kind release` command: every
  entry of the version, in English, grouped by category, with links to the PRs and the docs. When
  `changes/<version>.json` is missing, GitHub generates the notes as before, with a warning.
- **NuGet**: `build/pack-local.sh` writes `artifacts/package-release-notes.txt` (`notes --kind
  package` command) and passes it to `dotnet pack`; `Platform/Packaging.props` puts it in the
  `PackageReleaseNotes` of every `HackerOs.*` package: the `developer` entries of the version and
  the link to the Release. Before the collection (local package), the `unreleased/` entries are used.
- **Changelogs**: `CHANGELOG.md` and `CHANGELOG-fr.md` only list published versions. The test
  `ReleaseNotesTests.The_changelogs_of_the_repository_are_up_to_date` fails when they no longer
  match `changes/`: never edit them by hand.
