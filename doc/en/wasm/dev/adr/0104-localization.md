# ADR 0104: localization (English and French, per-process `LANG`, `locale.conf`, `.resx` resources)

## Status

Accepted on 2026-10-17 (work item `v3/1-adr`, for the work item `v3/12-localization`).

- **Complements:** [ADR 0050](0050-shell-grammar-and-process-environment.md)
  (process environment, `LANG=en_US.UTF-8` by default), ADR 0044
  (settings as files), ADR 0047 (`localizations` field of the manifest).
- **Replaces:** the never-implemented design of `docs/old/localization.md`
  (five cultures, `/etc/hackeros/locale.json`).
- **Plan:** [`wasm-v3-completion-plan.md`](../plans/wasm-v3-completion-plan.md),
  line `v3/12-localization`; gap R10; Benoit's answer 3 (English and
  French, English by default).

## Context

- No `.resx`, no `IStringLocalizer`: strings are hard-coded, in
  English and French mixed.
- `LoginEnvironment` (`Platform/HackerOs.Platform.Core/Processes/LoginEnvironment.cs`)
  sets `LANG=en_US.UTF-8`, then applies `/etc/environment`. Nothing reads `LANG`.
- `AppManifest.Localizations` (`LocalizationManifest(Culture, ResourcePath)`)
  exists and is empty everywhere.
- On Linux, the language follows `LANG` and `LC_*` of each process, initialized
  from `/etc/locale.conf` (system, `localectl`) then `~/.config/locale.conf`
  (user); a desktop language change takes effect at the next login.

## Decision

### 1. Languages

- Two languages: **English** (`en`, neutral language, used by default and as a
  fallback) and **French** (`fr`). Any `en_*` culture gives English, any
  `fr_*` culture gives French (`fr_CA`, `fr_FR`…), any other gives English.
- The full culture (for example `fr_CA`) is still used for the **format** of
  dates, numbers and currencies.

### 2. The language comes from the process environment

- Order at login: default `en_US.UTF-8` → `/etc/locale.conf` →
  `/etc/environment` → `~/.config/locale.conf`. The `locale.conf` files
  have the systemd format (`LANG=fr_CA.UTF-8`, `LC_TIME=…`).
- Variables read: `LC_ALL`, then `LC_MESSAGES` (texts) or `LC_TIME` /
  `LC_NUMERIC` (formats), then `LANG`, like glibc.
- Each process has its own culture, taken from **its** environment:
  `LANG=en_US.UTF-8 ls` displays its messages in English even if the session is
  in French.
- The SDK exposes it through `IAppExecutionContext.Culture` (texts) and
  `.FormatCulture` (formats), and through `HackEnvironment`. This is the plan's
  "language gateway".
- The desktop, taskbar, launcher and platform dialogs
  take the user's language **at login**. Changing the language in
  Settings writes `~/.config/locale.conf` and indicates that the change takes
  effect at the next login, as under GNOME. Apps launched afterwards
  in the same session keep the session environment.
- New command `localectl`: `status`, `list-locales`
  (`en_US.UTF-8`, `en_CA.UTF-8`, `fr_FR.UTF-8`, `fr_CA.UTF-8`…), and
  `set-locale LANG=…` (administrator, writes `/etc/locale.conf`).

### 3. Resources

- Each project has its `.resx` resources (`Resources/<Type>.resx` in English,
  `<Type>.fr.resx` in French) and uses `IStringLocalizer<T>`
  (`Microsoft.Extensions.Localization`).
- The localizer handed to an app is **bound to its process's culture**: it
  reads `ResourceManager.GetString(key, process culture)`, without depending on
  `CultureInfo.CurrentUICulture`, which is shared by the whole tab.
- Key missing in French: English text, and a debug warning once per key.
- The hosts declare `SatelliteResourceLanguages=en;fr`. The satellite assemblies
  of lazily loaded apps are loaded with them; if Blazor's lazy
  loader does not do so, `v3/12` loads the `fr` satellite
  explicitly along with the app assembly (this is the first point to prove).

### 4. App name and description

- The manifest generator reads `Resources/AppManifest.fr.resx` (keys `Name`,
  `Description`, `Terminal.Usage`) and produces the asset `localization/fr.json`
  with the entry `localizations: [{ "culture": "fr", "resourcePath": … }]`.
- The launcher, taskbar, Settings and `help` display the name in the
  user's language.

### 5. What is translated

- The platform (desktop, taskbar, launcher, dialogs, notifications,
  login and error screens), the system apps and the command messages
  (help, usage, errors).
- **Not translated**: outputs that scripts read (columns of `ls -l`,
  `ps`, `df`, `top`, `systemctl list-units`), system file and folder
  names (`/home`, `~/.config`, `Trash`), the logs in
  `/var/log` (in English, for tools), error codes.
- User folders keep their English names: no `xdg-user-dirs` style translation
  (`~/Downloads` does not become `~/Téléchargements`).

### 6. Tests

- A test checks that each `.fr.resx` has all the keys of its `.resx`, and
  conversely.
- An E2E test logs in with `~/.config/locale.conf` set to French and checks the
  launcher, an app and a command.

## Consequences

- Longest work item of the plan: it can be split into an infrastructure
  thread (§2 to §4) then one thread per app group (plan, §6).
- `docs/old/localization.md` receives a pointer to this ADR at the top (archive, the
  content is not rewritten); `process-environment.md`, `sdk-reference.md`,
  `sdk-developer-guide.md` ("translate your app") and `linux-commands.md` are
  updated.
