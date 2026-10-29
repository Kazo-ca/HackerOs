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
file in `changes/unreleased/`, unless it has the `no-changelog` label. To run it before pushing:

```bash
wasm2/HackerOs/build/tools/check-change-entry.sh origin/main HEAD
```

In a series, each work item PR to `<series>/base` adds its entry: the final PR to `main` brings
them all.

## When a version is released

The entries of `unreleased/` are grouped into `changes/<version>.json` in the PR that changes
`build/Versions.props` (script `build/tools/collect-changes.sh`, coming with work item 4 of
ADR 0186). Until then, do it by hand: create
`{ "version": "x.y.z", "date": "yyyy-mm-dd", "entries": [ … ] }` with the entries, without
changing their IDs, and delete the files of `unreleased/`.
