> [Version française](../../../../fr/wasm/dev/adr/0208-hack-store-sources-trust-build.md)

# ADR 0208: trusted sources, install for oneself and two-step approval, implementation adjustments

## Status

Accepted on 2026-10-30, work item `todo/3-hackstore-sources-trust` (`TODO`
issue [#248](https://github.com/Kazo-ca/HackerOs/issues/248), Hack Store 3).
Implements [ADR 0189](0189-app-sources-trust-and-per-user-install.md). Changes
none of its decisions but one: the catalog is not built "when the session
opens" but when the page starts, with a per-session hiding (§3 below). Benoit
was asked this question with its choices (Q1); the recommendation is applied
while waiting for his answer.

## Context

ADR 0189 decides the trusted sources, the install for oneself, the block list
and the two-step approval window. What remained to settle: how untrusted
sources are written in the file, how the host gives its defaults, what
`install` picks without an option, how a catalog built once per page (ADR 0182
Q5 C) becomes "specific to the user", and which authentication the edits of the
sources and of the block list ask for.

## Decision

1. **Sources file.** `[trusted]` keeps the `key=true|false` form. A source
   added "untrusted" is written `false`: it stays listed (and removable)
   without being trusted. Accepted keys: `catalog:<name>`, `github.com/<owner>`,
   `github.com/<owner>/<repo>`. `[catalogs]` only accepts `https` addresses
   without credentials. `[updates] interval-hours` goes from 1 to 720. Any
   invalid line is ignored with a warning in `/var/log/syslog`. A GitHub
   repository is never "Verified" on its own: that level is kept for the apps
   of the official catalog installed by the Store (Hack Store 4, issue #249).
2. **Compile-time values.** The host (`HackerOsAppHost=true`) sets MSBuild
   properties that `build/HackerOs.Apps.targets` turns into `AssemblyMetadata`
   (prefix `HackerOs.AppPackages.`) of its assembly;
   `AppPackageDefaults.FromAssembly` reads them at startup. An empty property
   keeps the standard value (official catalog, `catalog:official` and
   `github.com/Kazo-ca` trusted, 6 h, nothing blocked):

   | Property | Example |
   |---|---|
   | `HackerOsAppCatalogs` | `official=https://…/catalog.json,school=https://…/catalog.json` |
   | `HackerOsTrustedAppSources` | `catalog:official,github.com/Kazo-ca` |
   | `HackerOsAppUpdateIntervalHours` | `6` |
   | `HackerOsBlockedApps`, `HackerOsBlockedAppCategories`, `HackerOsBlockedAppCapabilities` | `social` |

   These values write `app-sources.config` and `app-policy.config` when they do
   not exist, and stay "built in" (trusted with no change by an administrator);
   an administrator can still set them to `false`.
3. **Per-session catalog (revises the "when the session opens" passage of ADR
   0189 §3 and Consequences).** The catalog is built once per page, before any
   sign-in. At startup, `InstalledAppsImage` loads the system apps, then the
   personal apps of every `/home/*` folder. Each session only sees its own:
   `InstalledPackageCatalog` knows whose each app is, `AppInstallationService`
   counts them as uninstalled for the others (launcher, search, associations,
   terminal) and `IAppManagementGateway.GetAllAsync` does not list them. If two
   users have different versions of the same app, the newest one is loaded for
   both (warning in the log). A personal copy of an app already installed for
   the system is ignored.
4. **Choice of scope.** `HackPackageScope.Auto` (no option) installs for
   oneself when allowed: a session is open, the source is trusted, the app is
   not blocked and not already installed for the system. Otherwise it moves to
   the install for everyone, which opens the two steps; an untrusted source
   already installed for oneself stays for oneself (update). `--user` refuses a
   blocked app (`apps.package.blocked`) or one already installed for the system
   (`apps.package.installed-for-system`). `hackpkg update` keeps each copy
   where it is.
5. **Two steps.** `IAppInstallApprovalPrompt` is the contract of step 2; the
   host provides it with `AppInstallApprovalBroker` and
   `AppInstallApprovalOverlay`, rendered by the desktop, the mobile mode and
   each secondary screen, like the authentication window (ADR 0055 §5). Step 1
   calls the authentication agent with `IsRootElevated=false`, even in a root
   process: no `sudo` replaces it. Without a window (a host without a UI),
   nothing is installed for everyone.
6. **Sources and block list: one authentication.** Adding or removing a source
   or a block-list rule asks for one administrator authentication (nothing
   under `sudo`), as `sudo nano` on these files already allows; there is no app
   to show in a step-2 window. Benoit was asked the question (Q2); the
   recommendation is applied.
7. **SDK.** `HackAppPackages.InstallAsync(source, HackPackageScope)`,
   `RemoveAsync(appId, HackPackageScope)`, `AddSourceAsync`,
   `RemoveSourceAsync`, `GetPolicyAsync`, `SetPolicyAsync`;
   `HackPackageSources.TrustedOwners` becomes `TrustedSources` (not shipped
   yet); `HackPackageTrust`, `HackPackageCatalog`, `HackPackagePolicy`,
   `HackPackageBlockKind`. The new members of `IAppManagementGateway` have a
   default implementation that throws `NotSupportedException`.

## Consequences

- The files of another user's personal app are in the page's runtime, even
  though that user does not see it: trust in the source and the block list
  stay the real barrier, as ADR 0189 already says.
- An app installed for oneself appears after a restart of HackerOS, like any
  installed app (ADR 0182 Q5 C).
- `--allow-untrusted` is gone; a script that used it gets "invalid option" and
  exit code 2.
- `hackpkg install <appId>` and `hackpkg info <appId>` (through the catalog)
  come with the Store (issue #249).

## Tasks

- [x] `app-sources.config`: `[catalogs]`, `[updates]`, untrusted sources,
      compile-time values (`AppPackageDefaults`, `HackerOs.Apps.targets`).
- [x] `app-policy.config`: reading, defaults, creation at startup (`FileSystemSeeder`).
- [x] Install for oneself, one copy at a time, per-session catalog (`InstalledPackageCatalog`).
- [x] Two-step approval window (`AppInstallApprovalBroker`, `AppInstallApprovalOverlay`).
- [x] `hackpkg`: `--user`, `--system`, `sources add/remove`, `policy`, `--allow-untrusted` removed.
- [x] Settings ▸ Apps ▸ Sources and block list (`AppSourcesPanel`).
- [x] Tests and documentation (`app-installation.md`, `linux-commands.md`).
