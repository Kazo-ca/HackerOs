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
5. Site address: **both, with a redirect**: `os.kazo.ca` becomes the main
   address, `hackos.kazo.ca` stays open to recover the data.

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
7. `rename/7-new-address`: move to `os.kazo.ca`. The browser sees two
   different sites (two origins), so the IndexedDB databases of
   `hackos.kazo.ca` are not visible from `os.kazo.ca`. `hackos.kazo.ca`
   keeps serving the OS: a visitor with no account is redirected to
   `os.kazo.ca`; a visitor who has data sees a "KazoOS has moved" notice
   that offers to make a backup (Settings ▸ Backup) then opens
   `os.kazo.ca`, where they restore it. GitHub Pages serves one domain per
   repository: `hackos.kazo.ca` moves to a second Pages site (separate
   repository) or stays this repository's domain with `os.kazo.ca` second;
   the work item picks and documents it.
8. After 3.0.0 is published: update the satellite repositories (app
   template, app marketplace, ported apps) to `Kazo.Os.*`.

## Consequences

- Every third-party app must move to `Kazo.Os.*` 3.0.0 and change its
  `using` directives and type names. The migration guide goes in
  `doc/{fr,en}/wasm/framework/`.
- On `hackos.kazo.ca`, existing users keep their data thanks to the
  migration of point 2. To take it to `os.kazo.ca`, they go through a
  backup (work item 7): nothing is transferred without them.
- GitHub redirects the old repository name to `Kazo-ca/Os`, but the links in
  the docs, issue templates and workflows are updated.
- Work item 3 changes almost every file path: a PR opened before it will
  need a rebase.
