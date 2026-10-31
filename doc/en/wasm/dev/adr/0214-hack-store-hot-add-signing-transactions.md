> [Version française](../../../../fr/wasm/dev/adr/0214-hack-store-hot-add-signing-transactions.md)

# ADR 0214: Hack Store, adding apps without a restart, package signing and transactional installs

## Status

Accepted on 2026-10-31, work item `todo/19-hackstore-later` (`TODO` issue
[#251](https://github.com/Kazo-ca/HackerOs/issues/251), work item 7 of the Hack
Store plan). Carries out answer B to Q5 of
[ADR 0182](0182-install-apps-from-github.md) ("later: adding without a
restart"), the signing that ADR 0182 left open ("Consequences") and question 1
of the `wasm-v3-completion-plan.md` plan (transactions, data migration). Changes
[ADR 0212](0212-hack-store-app-build.md) §3: "Restart to finish" now only serves
apps that are already loaded. Five questions were asked to Benoit (Q1 to Q5
below); he chose answer A to each on 2026-10-31.

## Context

- The app catalog (`AppCatalog`) is built once at startup, with the built-in apps
  then the installed apps (ADR 0182 §5, ADR 0208). About eighty places read it;
  nearly all of them read its properties again on each call, two lazy-loading
  registries take a list of assemblies from it when they are built.
- An assembly loaded in `AssemblyLoadContext.Default` cannot be unloaded: a new
  version of an app already started cannot replace the old one in the same page.
- Packages are only protected by the SHA-256 that `index.json` or `catalog.json`
  publishes: a compromised repository can publish another package with its
  SHA-256.
- .NET in WebAssembly has neither ECDSA nor RSA; passkey verification already
  goes through Web Crypto (`webAuthn.js`, ADR 0168).
- `CommitAsync` wrote the files, then the `[installed]` line, then deleted the
  old version: an interruption left the old version in place, but also a half
  written folder, and nothing allowed going back to the previous version or
  protecting a user's data from a failed migration.
- The manifest already carries `update.minimumUpgradableVersion` and
  `update.migrationIds`, which nobody read.

## Decision

### 1. Adding without a restart (ADR 0182, Q5 B)

1. After a successful install, update, removal or rollback, the OS reads the
   installed apps again (like `update-desktop-database` after `apt install`) and
   updates the running catalog: `AppCatalog` keeps its instance, its content is
   replaced in one go (`AppCatalog.Revision`, `AppCatalog.Changed` event). The
   Start menu, the mobile home screen, search, "Open with", the terminal
   (`PATH`) and embeddable components see the change at once; the lazy-loading
   registries learn the new assemblies.
2. **(Q1 A)** An app with an assembly already loaded in the page keeps its
   version until the restart: its update, removal or rollback waits for
   "Restart to finish" (ADR 0212 §3), which now only serves them. An app never
   started since the boot changes at once.
3. A service app added without a restart is not started: it starts at the next
   boot, like the other services.
4. The install report says whether the app is ready
   (`HackPackageInstallReport.RestartRequired`); `hackpkg` prints "available now"
   or "restart HackerOS to finish". `HackInstalledPackage.Loaded` stays true after
   the removal of a loaded app: `hackpkg` and the Store then say to restart.
5. Implementation: `InstalledAppsBoot.Live` is the `AppCatalog` instance every
   service receives; it is never the built-in catalog itself
   (`AppCatalog.CreateLiveCopy`). After a successful write, the gateway calls
   `IInstalledAppsActivator`, which reads the installed apps again
   (`InstalledAppsBoot.ReloadAsync`), keeps the loaded version of an app with an
   assembly in `AssemblyLoadContext.Default`, replaces the catalog's content
   (`AppCatalog.ReplaceContents`) and adds the new assemblies to the lazy-loading
   registry (`BuildKnownAssemblyLoaderRegistry.AddKnownAssemblies`).
   `AppEnablementRegistry.Changed` relays `AppCatalog.Changed`, and
   `AppInstallationService` reads its files again on each change.

### 2. Package signing

1. **Algorithm**: ECDSA P-256 with SHA-256, IEEE P1363 signature (`r||s`) in
   base64, over the bytes of the `.hackapp` file. Public key in
   SubjectPublicKeyInfo (DER) form, base64. Verification goes through Web Crypto
   in the browser, through `System.Security.Cryptography.ECDsa` elsewhere
   (tests, server).
2. **Where**: optional `signature` field of each version, in `index.json` (app
   repository) as in `catalog.json` (catalog).
3. **Keys**: new `[signing-keys]` section of `/etc/hackeros/app-sources.config`,
   one line per trusted source, several keys separated by commas to change keys
   without a gap:

   ```text
   [signing-keys]
   catalog:official=MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…
   ```

   Keys compiled into the host come from the `HackerOsAppSigningKeys` MSBuild
   property (like the other defaults of ADR 0208) and stay required even if the
   line is removed from the file; an administrator can add more with
   `sudo nano`. An invalid key is skipped with a warning.
4. **(Q2 A) Rule**: a source with at least one key requires a valid signature by
   one of them; otherwise the package is refused (`apps.package.unsigned`,
   `apps.package.bad-signature`) before any administrator is asked. A source
   without a key is accepted as before (SHA-256 only); the approval window shows
   "Not signed (checked by its SHA-256 only)", `hackpkg info` shows "Signature: none (SHA-256 only)".
   The keys of a `catalog:<name>` source are those of `catalog:<name>`; those of
   a `github:` repository are those of `github.com/<owner>` and of
   `github.com/<owner>/<repo>`.
5. **(Q3 A) Official catalog**: the `HackerOs.MarketPlace.Tool` tool of the
   `Kazo-ca/HackerOs-AppMarketPlace` repository gains `keygen` (creates the pair,
   writes the PKCS#8 private key to a local file, prints the public key) and
   `sign`; the `publish.yml` workflow signs each package with the
   `HACK_STORE_SIGNING_KEY` secret when it exists. Benoit creates the pair on his
   computer; the private key never goes through an agent. As long as the public
   key is not in `HackerOsAppSigningKeys`, nothing is required.

### 3. Transactional install

Like `dpkg` and its `updates/` folder:

1. Before writing, the OS writes a journal `<root>/<appId>/.transaction`
   (`operation`, `version`, `previous`, `startedAt`), where `<root>` is
   `/usr/lib/hackeros/apps` or `~/.local/share/hackeros/apps`.
2. Files are written to `<root>/<appId>/<version>.partial/`, then the folder is
   renamed `<version>/` in one operation.
3. The `[installed]` line is the commit point: as long as it has not changed,
   the old version stays the OS's version.
4. Only then does the OS delete what is no longer kept (§4) and the journal.
5. **Recovery**: before any install, any removal and on `hackpkg repair`, the OS
   reads the journal of the app concerned (all of them for `repair`). If
   `[installed]` already names the new version, it finishes the clean-up;
   otherwise it deletes `.partial` and the new folder and keeps the old version.
   A line of `/var/log/syslog` says what was done. At boot, a `.partial` folder
   or a version that is not listed is never loaded.
6. `hackpkg repair` repairs the user's apps; `hackpkg repair --system` also
   repairs the system's, after an administrator authenticated. It also deletes
   `.partial` folders without a journal and the folder of an app that no
   `[installed]` line names any more.
7. `update.minimumUpgradableVersion` is enforced: an older installed version
   refuses the in-place update (`apps.package.reinstall-required`: remove, then
   install).

### 4. Rollback and user data

1. **(Q4 A)** An update keeps the version it replaces (only one):
   `<root>/<appId>/<previous>/`. `hackpkg rollback <appId>` (and the Store's
   "Back to <version>" button on an installed app) puts that version back in
   `[installed]`, with the same approvals as an install of the same scope
   (two-step approval for the system or an untrusted source, operation
   `AppInstallApprovalOperation.Rollback`), and keeps the version it leaves in
   turn. A removal deletes every version.
2. **(Q5 A) Backup and migration**: each user has
   `~/.config/hackeros/app-data-versions.config` (`[versions] <appId>=<version>`,
   the version that used their data last). When an installed app whose version
   differs from the recorded one is launched, before the process exists, the OS:
   - copies `~/.config/<appId>/` and `~/.local/share/<appId>/` to
     `~/.local/share/hackeros/app-backups/<appId>/<old version>/`;
   - if the new version is older (rollback) and a backup of that version
     exists, puts that backup back in place of the data;
   - otherwise (update), gives the process the variable
     `HACKEROS_PREVIOUS_VERSION=<old version>`; the app migrates its data itself,
     like `onUpgrade` on Android;
   - records the new version. At most two backups are kept per app: the one of
     the version left and the one of the version launched.

   The backup (`AppDataGuard`) copies `~/.config/<appId>/` to `<backup>/.config/`
   and `~/.local/share/<appId>/` to `<backup>/.local-share/`. A failed backup
   (full disk) is written to the log and does not stop the launch; the version
   is then not recorded, and the next launch tries again.
3. Everything runs with the user's rights, in their home: no other user's data
   is read. No SDK addition is needed (`HackEnvironment.GetEnvironmentVariable`).

### 5. SDK

`HackPackageInstallReport.RestartRequired` and `.Signed`,
`HackPackageInfo.SignatureRequired`, `HackInstalledPackage.PreviousVersion`,
`HackAppPackages.RollbackAsync` and `HackAppPackages.RepairAsync` in
`PublicAPI.Unshipped.txt` (next minor). The new `IAppManagementGateway` members
(`RollbackPackageAsync`, `RepairPackagesAsync`) have a default implementation
that throws `NotSupportedException`.

## Consequences

- An app installed from the Store or `hackpkg` can be used at once, except the
  update of an app already started.
- Any code that keeps a copy of the catalog must listen to `AppCatalog.Changed`;
  the views that list apps do so through `AppEnablementRegistry.Changed`, which
  now relays the catalog's changes.
- The official catalog is only really protected once Benoit created the key and
  it is compiled into the host (`OfficialCatalogSigningKey` or
  `HackerOsAppSigningKeys`); the versions already published must be published
  again to get their signature before that OS version.
- Signing an app repository (`github:`) is possible with the same format; a tool
  for authors is still to do (`TODO` issue
  [#321](https://github.com/Kazo-ca/HackerOs/issues/321)).
- An update takes two versions on disk; data backups count in the user's quota.

## Questions asked to Benoit

| Question | Choices | Benoit's answer (2026-10-31) |
|---|---|---|
| Q1 Update of an app already open | A restart for that one, the rest live; B every update waits for the restart | A |
| Q2 Source with a key, unsigned package | A refuse; B warn | A |
| Q3 Key of the official catalog | A Benoit creates it with `keygen`, signing in `publish.yml`; B later (`TODO` issue) | A |
| Q4 Previous version kept | A yes, `hackpkg rollback`; B no | A |
| Q5 Data migration | A the OS backs up, the app migrates (`HACKEROS_PREVIOUS_VERSION`); B named migrations run by the OS | A |

## Tasks

- [x] Live catalog (`Revision`, `Changed`), extensible lazy registries, installed apps read again after each change.
- [x] Apps already loaded: change kept for the restart; `RestartRequired`.
- [x] `[signing-keys]`, `HackerOsAppSigningKeys`, `signature` field, verification (Web Crypto, `ECDsa`).
- [x] `.transaction` journal, `.partial` folder, recovery, `hackpkg repair`, `minimumUpgradableVersion`.
- [x] Previous version kept, `hackpkg rollback`, Store button.
- [x] Data backup at launch, `HACKEROS_PREVIOUS_VERSION`.
- [x] `keygen`, `sign` and signing in `publish.yml` (AppMarketPlace repository).
- [x] Tests and documentation (`app-installation.md`, `linux-commands.md`, `sdk-reference.md`).
