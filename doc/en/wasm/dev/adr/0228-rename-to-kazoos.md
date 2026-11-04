> [Version française](../../../../fr/wasm/dev/adr/0228-rename-to-kazoos.md)

# ADR 0228: HackerOS becomes KazoOS

## Status

Accepted on 2026-11-04 (asked by Benoit, series `rename/base`, branch
`rename/1-adr-kazoos`).

- **Touches:** the name in every ADR, doc, project, package and type that
  carries "HackerOS", "HackerOs" or the `Hack` prefix. Accepted ADRs are not
  rewritten: they keep the old name, and this ADR gives the mapping.

## Context

Benoit (2026-11-04): "I would like to find a new name ... Maybe KazoOs?
HackerOs is already taken I think".

Research done the same day (`/mnt/project-files/rename/nom-du-projet.md` in
the project):

- **HackerOS is taken** by an active Debian-based Linux distribution (GitHub
  organization `HackerOS-Linux-System`, version 4.x, ISOs on SourceForge,
  press articles). It ranks above this repository in GitHub search;
  `hackeros.com` is for sale at a reseller.
- **KazoOS is free**: no product or repository with that name, no `Kazo.*`
  NuGet package. The domains `kazoos.com/.ca/.org` are taken (the word
  "kazoo"); `kazoos.dev` and `kazoos.app` looked free.
- "Kazo WebOS" was set aside: webOS is a registered trademark of LG.

Benoit's answers:

1. Displayed name: **KazoOS**.
2. Scope: **rename everything**, including the `Hack` prefix of the SDK
   types, replaced by **`Kazo`** ("3, just take Kazo to replace the `Hack`
   prefixes").
3. Namespaces and packages: **`Kazo.Os.*`**.
4. He renames the repository `Kazo-ca/HackerOs` to **`Kazo-ca/Os`** himself.

## Decision

### 1. Name mapping

| Before | After |
|---|---|
| HackerOS (displayed name, title, PWA, docs) | KazoOS |
| `HackerOs.*` (projects, assemblies, namespaces, NuGet packages) | `Kazo.Os.*` (e.g. `Kazo.Os.AppSdk`, `Kazo.Os.Platform.Core`) |
| `HackerOs.sln`, folder `wasm2/HackerOs/` | `Kazo.Os.sln`, folder `wasm2/KazoOs/` |
| `Hack*` types (`HackFile`, `HackPath`, `HackExecutionContext`...) | `Kazo*` (`KazoFile`, `KazoPath`, `KazoExecutionContext`...) |
| `HackerOs*` / `Hacker*` types (`HackerOsVersion`, `HackerIcon`...) | `KazoOs*` / `Kazo*` (`KazoOsVersion`, `KazoIcon`...) |
| Apps `HackPaint`, ... | `KazoPaint`, ... |
| Commands `hackpkg`, `hackweb`, `hackwebd` | `kazopkg`, `kazoweb`, `kazowebd` |
| ASP.Hack | ASP.Kazo |
| Hack Store | Kazo Store |
| Extensions `.hackapp`, `.hacktheme`, `.hackeros` | `.kazoapp`, `.kazotheme`, `.kazoos` |
| Files `/etc/hackeros`, `~/.config/hackeros`, `/etc/hackweb`, `/var/log/hackweb` | `/etc/kazoos`, `~/.config/kazoos`, `/etc/kazoweb`, `/var/log/kazoweb` |
| IndexedDB databases `hackeros`, `hackeros-*`, `hackweb.*` | `kazoos`, `kazoos-*`, `kazoweb.*` |
| Repositories `HackerOs-App-*`, `HackerOs-AppTemplate`, `HackerOs-AppMarketPlace` | renamed by Benoit; their code follows the `Kazo.Os.*` packages |

The sentence "Aucun jeu sur ce repository" (ADR 0135) does not change.

### 2. Compatibility

- **SDK 3.0.0.** The rename breaks the whole public API: the `Kazo.Os.*`
  packages start at 3.0.0. The 43 `HackerOs.*` packages stay on nuget.org,
  marked deprecated with a pointer to their replacement; no transition
  package is published. The `Kazo.Os.` prefix is reserved on nuget.org (ID
  prefix reservation).
- **User data.** On the first KazoOS boot, a one-time migration copies each
  `hackeros*` / `hackweb.*` database to its new name, moves the
  configuration files of the virtual file system to their new paths,
  renames the `localStorage` keys, then deletes the old ones. It can be
  replayed: if interrupted, it resumes on the next boot. A test covers a
  database in the current format (`Tests/fixtures/indexeddb`).
- **Existing files.** The old extensions (`.hackapp`, `.hacktheme`,
  `.hackeros`) are still read and opened; KazoOS writes only the new ones.
  The old commands (`hackpkg`...) stay as an alias that prints a warning,
  for one minor version.
- **Docs.** Current docs say KazoOS. Accepted ADRs and docs under `old/`
  keep their text; their links to renamed files are fixed.

### 3. Work items of the `rename/base` series

Each work item has its branch `rename/<n>-<topic>` and its PR to
`rename/base`. Work items 3 and 4 touch almost every file: they run one after
the other, not in parallel.

1. `rename/1-adr-kazoos`: this ADR.
2. `rename/2-display-name`: displayed name (title, `manifest.webmanifest`,
   sign-in screen, About), readmes, `AGENTS.md`, `CLAUDE.md`, `features.md`,
   text of the current docs.
3. `rename/3-namespaces`: folders, `.csproj`, `.sln`, namespaces, package
   IDs, build scripts, GitHub Actions workflows, `dotnet new` templates.
4. `rename/4-type-prefix`: `Hack` → `Kazo` type prefix, `PublicAPI.*.txt`
   files, compatibility fixture `Tests/fixtures/sdk-compat`, version 3.0.0.
5. `rename/5-system-names`: commands, extensions, file system paths,
   ASP.Kazo, Kazo Store, still reading the old names.
6. `rename/6-storage-migration`: migration of the IndexedDB databases, the
   configuration files and `localStorage`, with its tests.
7. After 3.0.0 is published: update the satellite repositories (app
   template, app marketplace, ported apps) to `Kazo.Os.*`.

## Consequences

- Every third-party app must move to `Kazo.Os.*` 3.0.0 and change its
  `using` directives and type names. The migration guide goes in
  `doc/{fr,en}/wasm/framework/`.
- Existing users keep their data thanks to the migration of point 2,
  provided the site address (the browser origin) does not change: IndexedDB
  databases belong to an origin. The site therefore stays on
  `hackos.kazo.ca` until Benoit decides otherwise.
- GitHub redirects the old repository name to `Kazo-ca/Os`, but the links in
  the docs, issue templates and workflows are updated.
- Work item 3 changes almost every file path: a PR opened before it will
  need a rebase.
