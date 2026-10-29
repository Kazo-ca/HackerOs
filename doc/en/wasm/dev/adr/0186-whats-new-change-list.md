> [Version française](../../../../fr/wasm/dev/adr/0186-whats-new-change-list.md)

# ADR 0186: change list and what's new since the last visit

## Status

Accepted on 2026-10-29. Decisions made with Benoit in the thread "Show what's new since the last
visit" (12 questions asked on 2026-10-28, answers on 2026-10-29: Q2 = C, Q8 = B, the others as
recommended). Plan: https://claude.ai/artifact/Q9BdEs6HxUCjiD1os1TPNq. Tracking: TODO issue
[#252](https://github.com/Kazo-ca/HackerOs/issues/252).

Implemented in work items (§9). Work item 1 (format, test, entries of 2.1.0 and of the next
version) and work item 5 (PR check) come with this ADR.

- **Complements:** [ADR 0173](0173-automatic-releases-from-the-version-file.md) (automatic
  release from `Versions.props`) and
  [ADR 0174](0174-announce-a-new-version-to-open-sessions.md) (`/version.json` and the
  new version banner).

## Context

Benoit wants to see in the OS what changed since the last visit, grouped by version, with a
**New** badge, and to reuse the same list for release notes (GitHub Release, NuGet).

State when the decision was made (main `0673428f`):

- The only version is `HackerOsVersion` in `wasm2/HackerOs/build/Versions.props` (2.1.0).
- `release.yml` publishes notes generated automatically by GitHub (PR titles).
- `/version.json` is only used by the "restart to update" banner (ADR 0174).
- Pages deploys `main` on every merge, while the version changes less often: a feature is online
  before it has a version number.
- Several threads work in parallel and merge often.
- The OS user interface is bilingual (`Strings.resx` and `Strings.fr.resx`).

## Decision

### 1. One file per change (Q1: A)

- Each change is a small file `wasm2/HackerOs/changes/unreleased/<id>.json`. Two PRs open at the
  same time never write to the same file: no merge conflict.
- When a version is released, all entries of `unreleased/` are grouped into
  `wasm2/HackerOs/changes/<version>.json`, with the date of the version, in the PR that changes
  `Versions.props` (script `build/tools/collect-changes.sh`, work item 4, like
  `ship-public-api.sh` for the public API).
- The ID of an entry never changes, even when it moves into a version file.

### 2. Format of an entry (Q2: C, Q3 to Q5: A)

```json
{
  "id": "44aa95c2-364a-41b2-8610-a7ad3a8f70fa",
  "category": "new",
  "audience": ["user"],
  "title": { "en": "Mount a computer folder as a network drive", "fr": "Monter un dossier de l'ordinateur comme lecteur réseau" },
  "description": { "en": "…", "fr": "…" },
  "pr": 238,
  "links": ["doc/en/wasm/dev/adr/0181-host-folder-mounted-as-a-network-drive.md"]
}
```

- `id`: a lowercase **GUID** (Benoit's choice). The file name in `unreleased/` is the ID. Since a
  GUID is not readable, the English title is the landmark in reviews.
- `category`: `new`, `improved`, `fixed` or `developer` (developers / SDK).
- `audience`: `user`, `developer`, or both. A `developer` entry has the `developer` audience only.
  The OS shows `user` entries, NuGet shows `developer` entries, the GitHub Release and
  `CHANGELOG.md` show everything.
- `title` (required) and `description` (optional) each have an `en` and a `fr` text. The OS
  shows the user's language, falling back to English. GitHub and NuGet get English.
- `pr` (PR number) and `links` (`https` URL or path of a file of the repository, from its root)
  are optional.
- No other property is allowed.

A version file has the form:

```json
{ "version": "2.1.0", "date": "2026-10-26", "entries": [ … ] }
```

The `ChangeEntriesTests` test (`Tests/HackerOs.Ecosystem.Tests`) checks all of this: schema, valid
GUID unique across `changes/`, file name equal to the ID or the version, known category and
audience, non-empty texts in both languages, links to files that exist, and no version file newer
than `HackerOsVersion`.

### 3. Changes deployed before their version (Q6: A)

The entries of `unreleased/` are shown as soon as they are deployed, under a group "Since 2.1.0
(next version)". When the version is released, they are simply filed under the new number. The
**New** badge is kept, because the "seen" state follows the ID, not the version.

### 4. In the OS (Q7, Q9, Q10: A)

1. **`/whats-new.json`**: the build of `OS/HackerOs.Ecosystem` assembles `changes/*.json` and
   `changes/unreleased/` into one file served at the root of the site, like `/version.json`
   (ADR 0174). Only entries whose audience contains `user` go into it.
2. **What's new service** (Platform.Core): it reads this file and the account's state, then
   computes the unseen entries.
3. **Per-account state**, in a real file: `~/.local/state/hackeros/whats-new.json`, which holds
   the list of seen IDs.
4. **End of the badge**: once the what's new page has been opened, the shown entries keep their
   badge during the visit and become "seen" when the page is closed.
5. **Discovery**: after sign-in, if unseen entries remain, a notification ("3 new things since
   your last visit", **View** button). The list is a page Settings ▸ About ▸ What's new: one group
   per version, newest first, entries sorted by category. A dot stays on the Start menu while
   unseen entries remain.

### 5. New account (Q8: B)

Without a rule, a new account would see the whole history as "New". Benoit's choice: a new
account sees what's new in the **latest version**. On the account's first start, the service
marks everything that exists as seen, **except** the entries of the latest released version and
those of the next version already deployed (`unreleased/`).

The "next version already deployed" part is our reading of the answer; it has been submitted to
Benoit and will be corrected here if he prefers the released version only.

### 6. PR check (Q12: A)

- The **Change list entry** workflow (`.github/workflows/changelog-entry.yml`) runs on every PR to
  `main`. It fails if the PR touches the product (`wasm2/HackerOs` outside `Tests/`, `docs/`,
  `changes/` and `.md` files) without **adding** a file in `changes/unreleased/`, unless the PR
  has the **`no-changelog`** label. The rule is in
  `wasm2/HackerOs/build/tools/check-change-entry.sh`, which can be run by hand.
- `no-changelog` is for what neither a user nor an app author can see: refactoring, tests, build,
  CI.
- Work item PRs to a `<series>/base` branch already add their entry, so the final PR to `main`
  passes the check. The check does not run on those PRs.
- Making the check required is set in the branch protection of `main` (GitHub).

### 7. Publications (Q11: A)

- `release.yml` builds the GitHub Release text from `changes/<version>.json` (English, all
  entries, grouped by category), instead of GitHub's notes.
- `pack-local.sh` fills `PackageReleaseNotes` of every `HackerOs.*` package with the version's
  `developer` entries, plus the link to the Release.
- `CHANGELOG.md` (English) and `CHANGELOG-fr.md` (French) are generated at the root of the
  repository, each with a link to the other on the first line, like the readmes.

### 8. History and Hack Store apps (Q12: A, Q13)

- Only **2.1.0** and **the next version** are filled in. 2.0.3 and 2.1.0 were published the same
  evening (first run of `release.yml`): `changes/2.1.0.json` therefore describes what arrived
  since the desktop series (SDK 2.0). Nothing is rebuilt for older versions.
- The format does not depend on the OS: a Hack Store app may one day provide its own list in the
  same format. Nothing is built for that in v1.

### 9. Work items

1. ADR 0186 + format: `changes/` folder, `ChangeEntriesTests`, entries of 2.1.0 and of the next
   version. **Done with this ADR.**
2. Served file + service: `/whats-new.json` at build time, what's new service, per-account state,
   new account rule. **Done** (`whats-new/2-served-file-and-service`: `Tools/HackerOs.Tools.ChangeList`,
   `WhatsNewService`, SDK `HackWhatsNew`).
3. User interface: Settings ▸ About ▸ What's new page, notification after sign-in, Start menu dot,
   English and French texts.
4. Publications: `collect-changes.sh`, Release text in `release.yml`, `PackageReleaseNotes`,
   `CHANGELOG.md` and `CHANGELOG-fr.md`.
5. PR check: **Change list entry** workflow and `no-changelog` label. **Done with this ADR.**

Each work item updates the docs in both languages and the README indexes.

## Consequences

- Every PR that changes the product writes an entry in English and in French. It is a small cost
  per PR, but release notes and the what's new page no longer have to be written separately.
- Entries are written for readers, not for code reviewers: a short sentence that says what the
  person can now do.
- Until work items 2 to 4 are done, entries are only read by the test and by humans; `release.yml`
  keeps GitHub's generated notes.
- Practical guide: [`change-list.md`](../change-list.md).
