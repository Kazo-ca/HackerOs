> [Version française](../../../fr/wasm/framework/localization.md)

# Localization (English and French)

Decisions: [ADR 0104](../dev/adr/0104-localization.md), adjusted by [ADR 0111](../dev/adr/0111-localization-implementation-adjustments.md). Work item: `v3/12-localization`
of the [wasm v3 plan](../dev/plans/wasm-v3-completion-plan.md).

## Goal

HackerOS speaks **English** (default and fallback language) and **French**. The
language follows the locale, as on Linux:

- each **process** has its own language, taken from **its** environment (`LANG`,
  `LC_MESSAGES`, `LC_ALL`): `LANG=en_US.UTF-8 ls` answers in English even in
  a French session;
- the **platform** (desktop, taskbar, launcher, dialogs, sign-in
  screens) takes the user's language **at sign-in** and keeps it
  until sign-out; before sign-in, it speaks the system language.

## Architecture

### Where the language is written

| File | Role | Written by |
|---|---|---|
| `/etc/locale.conf` | System locale (sign-in screen, users with no setting) | `sudo localectl set-locale LANG=fr_CA.UTF-8`, created by the seeder with `LANG=en_US.UTF-8` |
| `/etc/environment` | Global variables (can also set `LANG`) | the administrator |
| `~/.config/locale.conf` | User locale | Settings → Language, or by hand |

systemd format: one `NAME=value` line per variable (`LANG`, `LANGUAGE`,
`LC_MESSAGES`, `LC_TIME`, `LC_NUMERIC`, `LC_MONETARY`...), `#` for
comments. `HackLocale.ParseLocaleConf` / `FormatLocaleConf` read and
write this format.

At sign-in, `LoginEnvironment.Build` applies in order: default
`LANG=en_US.UTF-8` → `/etc/locale.conf` → `/etc/environment` →
`~/.config/locale.conf`. The resulting environment is copied into each process
(ADR 0050).

### How the language is read

- `HackLocale.ResolveLanguage(getVar)`: `LC_ALL`, then `LC_MESSAGES`, then
  `LANG`; `fr_*` → `fr`, everything else → `en`.
- `HackLocale.ResolveFormatCulture(getVar, LC_TIME)`: the full culture
  (`fr-CA`) for dates, numbers and currencies; `C`/`POSIX` → invariant
  culture.
- Apps: `IAppExecutionContext.Culture` and `.FormatCulture` (default members,
  read from the process environment on each call), and
  `HackEnvironment.Culture` / `.FormatCulture` for the ambient context.
- Platform: `SessionLocaleService` (Platform.Core), with `Culture`,
  `FormatCulture`, `LocaleName` and the `Changed` event. `App.razor` calls
  `UseSystemLocaleAsync()` at startup and `UseSessionLocaleAsync(principal)`
  after each sign-in.

`CultureInfo.CurrentUICulture` is **never** used to choose the language:
it is shared by the whole tab, whereas two processes can have two
languages.

### Localizers

- `HackStringLocalizer` (AppSdk): an `IStringLocalizer` over a
  `ResourceManager`, with a function that gives the culture on each read.
  Arguments (`L["Clé", n]`) are formatted with the text's culture. A key
  missing in French gives the English text and a debug warning,
  once per key.
- `AppContext.GetLocalizer<Strings>()`: localizer bound to the app's
  **process**. `HackLocalization.GetLocalizer<Strings>()` with no argument reads the
  ambient context.
- `SessionStringLocalizer<T>` (Platform.Core): what
  `@inject IStringLocalizer<T>` receives in a platform component, bound to
  `SessionLocaleService.Culture`. `AddHackerOsPlatformLocalization()` declares
  it (called by `AddHackerOsEcosystem`, hence for the three hosts).
- `EnglishStringLocalizer<T>` (AppSdk): always English, for component
  tests: `services.AddSingleton(typeof(IStringLocalizer<>), typeof(EnglishStringLocalizer<>))`.

### Satellites in the browser

The hosts declare `SatelliteResourceLanguages=en;fr` and the ICU data
`icudt_EFIGS.dat` (`Directory.Build.props`). The `loadAllSatelliteResources`
startup option is not enough for lazily loaded apps
(`GetSatelliteAssembly` throws `FileNotFoundException`). HackerOS therefore calls the
runtime function `INTERNAL.loadSatelliteAssemblies(["fr"])`, as Blazor does
to change culture:

- on the main thread, `BrowserSatelliteAssemblies.LoadAsync` at host startup
  (Infrastructure.Browser);
- in Web Workers, `BrowserWorkerAssemblyLoader.LoadLazyAsync`, once.

An app installed by `hackpkg` or the Hack Store has its satellites in its
package (`lib/<culture>/<assembly>.resources.dll`, put there by `HackerOsPack`);
the OS loads them before the app's first render, for each HackerOS language
(`InstalledAppSatellites`, [ADR 0222](../dev/adr/0222-installed-app-satellite-assemblies.md)).
Nothing to declare: a `Resources/Strings.fr.resx` is enough, as for an app
compiled into the OS.

### App name and description

The manifest generator reads `Resources/AppManifest.<culture>.resx`:

| Key | Replaces |
|---|---|
| `Name` | `AppManifest.Name` |
| `Description` | `AppManifest.Description` |
| `Terminal.Usage` | `TerminalCommandManifest.Usage` |

In a project with several apps (the commands), each key is prefixed with the
id: `org.hackeros.commands.ls.Description`.

The titles of file menu entries (ADR 0159 §3) also live in these
files, but separately: English in the **neutral** file
`Resources/AppManifest.resx` (required, otherwise `HOSAPP0026`), translations
in `AppManifest.<culture>.resx`. The generator copies them into the manifest's
`contextMenus` (`titles`) and not into `localization/<culture>.json`.

It writes `localization/<culture>.json` (one app) or
`localization/<appId>/<culture>.json` (several apps), in the strict format
`{"name","description","terminalUsage"?}`, adds the entry
`localizations: [{"culture":"fr","resourcePath":"localization/fr.json"}]` and
deletes stale files. Error: `HOSAPP0040`. The aggregator copies these
texts into `BuiltInApplications.json` (optional `localized` property), and
`AppCatalog.Localize(manifest, culture)` applies them. The launcher, window
titles, `help` and `IAppCatalogGateway` thus display the translated name.
The id, the command name and the aliases are never translated.

The `localization/**` files are not declared as manifest assets:
their texts travel in `BuiltInApplications.json`.

## Translating your code (guide)

1. **Marker class.** An `internal sealed class Strings` class (private
   constructor) in the project's root namespace. The resources are
   `Resources/Strings.resx` (English) and `Resources/Strings.fr.resx`
   (French). A subfolder gives a sub-namespace:
   `HackerOs.Commands.Ls.Strings` ↔ `Resources/Ls/Strings.resx`. The resource
   name follows the rule `<assembly>.Resources.<type name without the assembly prefix>`.
2. **Writing the `.resx` files.** `build/tools/resx.py output.resx table.json` writes
   a sorted `.resx` from a JSON table `{"Key": "Text"}`. Both files
   have **exactly** the same keys (a test checks it).
3. **Keys.** PascalCase, grouped by area with a dot:
   `Toolbar.NewFolder`, `Dialog.Delete.Title`, `Error.NotFound`. Arguments
   are `{0}`, `{1}`; a literal brace is written `{{`.
4. **Reading the texts.**
   - Windowed app: `private IStringLocalizer<Strings> L => _l ??= AppContext.GetLocalizer<Strings>();`
     then `@L["Clé"]`.
   - Command: `IStringLocalizer<Strings> text = context.App.GetLocalizer<Strings>();`.
     The glibc-style file error reasons ("Aucun fichier ou dossier de ce
     nom") come from `CommandText.Reason`/`FileError`/`Describe`
     (`HackerOs.Commands.Common`), and `ShellErrorText.Describe(exception, context)`
     gives them in French for a third-party app.
   - Platform component: `@inject IStringLocalizer<Strings> L`.
   - SDK library (`AppSdk.Blazor`, `FileView`, `Terminal`): the
     app context received as a cascading value, `AppContext.GetLocalizer<Strings>()`, or
     `HackLocalization.GetLocalizer<Strings>()` outside a component.
   - Platform code outside a component (Platform.Core):
     `Strings.For(culture)[...]`.
5. **Dates and numbers**: `FormatCulture` (app) or
   `SessionLocaleService.FormatCulture` (platform), never the thread's culture.
6. **French typography**: a space before `:`, `?`, `!` and `;`, quotation marks
   `« … »`, capital letter only on the first word ("Nouveau dossier").
7. **What stays in English** (ADR 0104 §5): output read by
   scripts (columns of `ls -l`, `ps`, `df`, `top`, `systemctl list-units`),
   file and folder names (`~/Downloads` stays as is),
   `/var/log` logs, error codes, command and
   option names, identifiers (capabilities, app ids, setting keys).
8. **App name**: `Resources/AppManifest.fr.resx` (keys from the table
   above), then a build regenerates `manifest.json` and `localization/`. These
   generated files are committed.

## Changing language

- **User**: Settings → Language writes `~/.config/locale.conf`
  (`LANG=fr_CA.UTF-8`), or "Follow the system language" deletes it. The
  change takes effect at the next sign-in, as on GNOME.
- **System**: `sudo localectl set-locale LANG=fr_FR.UTF-8`; `localectl`
  displays the system locale, `localectl list-locales` the 8 known locales.
- **A single process**: `LANG=fr_FR.UTF-8 help`.

## Key decisions

- Language per process, never per tab (`CurrentUICulture` ignored).
- Platform frozen during the session: a change to `~/.config/locale.conf`
  waits for the next sign-in.
- Satellites loaded by `INTERNAL.loadSatelliteAssemblies`, not by the startup
  option.
- Translated manifest texts embedded in `BuiltInApplications.json`
  rather than read as assets at runtime.

## Task list

- [x] `HackLocale`: glibc rules, `locale.conf`, known locales.
- [x] `LoginEnvironment`: `/etc/locale.conf` and `~/.config/locale.conf`; seeder.
- [x] `IAppExecutionContext.Culture` / `.FormatCulture`, `HackEnvironment`.
- [x] `HackStringLocalizer`, `GetLocalizer<T>`, `EnglishStringLocalizer<T>`.
- [x] `SessionLocaleService`, `SessionStringLocalizer<T>`, `AddHackerOsPlatformLocalization`.
- [x] `fr` satellites loaded in the browser (main thread and workers).
- [x] Generator: `AppManifest.<culture>.resx` → `localization/`, `HOSAPP0040`; `localized` aggregator.
- [x] Catalog, launcher, window titles, `IAppCatalogGateway` localized.
- [x] Shell: builtins and `help` in the process's language.
- [x] `localectl` command.
- [x] Settings Language panel.
- [x] Platform translation (desktop, taskbar, launcher, dialogs, sign-in).
- [x] Translation of system apps and commands.
- [x] Tests: `.resx` parity, `HackLocale`, generator, E2E in French.
