> [Version française](../../../../fr/wasm/dev/adr/0189-app-sources-trust-and-per-user-install.md)

# ADR 0189: trusted app sources, per-user install and administrator validation

## Status

Accepted on 2026-10-29 by Benoit, in the "Plan the Hack Store" thread (answers
at the end). This ADR writes no code. **It revises
[ADR 0182](0182-install-apps-from-github.md)**: its "machine-wide install only,
administrator required" rule (Q4) is replaced by §3 and §4 below; the rest of
ADR 0182 stands. It serves the Hack Store of
[ADR 0188](0188-hack-store-and-app-catalog.md) and `hackpkg`.

## Context

- `/etc/hackeros/app-sources.config` (ADR 0182) lists the hosts contacted and
  the trusted GitHub owners (`github.com/Kazo-ca`). An app from another source
  needs `--allow-untrusted` and an administrator.
- Every install is machine-wide today, under `sudo`.
- An installed app's code runs in the same runtime and origin as the OS: trust
  in the source remains the real barrier.
- Benoit wants: every app of our catalog trusted (reviewed and built by us); a
  user who installs for themselves without an administrator from a trusted
  source; an administrator block list; a two-step validation, every time, for a
  system-wide install.

## Decision

### 1. Sources

Two kinds of sources, in `/etc/hackeros/app-sources.config`:

```text
[hosts]
github=raw.githubusercontent.com

# Catalogs (ADR 0188). Name = address of catalog.json.
[catalogs]
official=https://raw.githubusercontent.com/Kazo-ca/HackerOs-AppMarketPlace/packages/catalog.json

# Trusted sources.
[trusted]
catalog:official=true
github.com/Kazo-ca=true

[updates]
interval-hours=6
```

- A source is a **catalog** (another repository in the ADR 0188 format), a
  single **GitHub repository**, or all repositories of an owner
  (`github.com/<owner>`).
- **Sources built into the OS**: the file's default values are set when the
  host is built (an MSBuild property of the host, by default the official
  catalog and `github.com/Kazo-ca`). They are trusted with nothing to do.
- **Sources added by an administrator**, without redeploying: through Settings ▸
  Apps ▸ Sources or `hackpkg sources add`, with administrator authentication.
  When adding, the administrator chooses "trusted" or not (**not** by
  default).

### 2. Trust levels shown to the user

- **Verified**: app from the official catalog, reviewed and built by us.
- **Trusted**: another source built into the OS or marked trusted by an
  administrator.
- **Untrusted**: any other source. Every install needs an administrator (§4),
  even for a single user.

### 3. Installing for yourself, without an administrator

- An app from a **Verified** or **Trusted** source installs for the current
  user **without an administrator**, unless the block list (§5) blocks it.
- Files in `~/.local/share/hackeros/apps/<appId>/<version>/`, recorded in
  `~/.config/hackeros/installed-apps.config` (an `[installed]` section, same
  shape as in `apps.config`).
- The app appears the next time that user's session opens (restart, ADR 0182
  Q5 C), and only for them. The app catalog therefore becomes partly specific
  to the session user.
- One copy at a time: an app already installed for the system cannot be
  installed for yourself; installing it for the system removes the personal
  copies (settings in `~/.config/<appId>/` stay).
- Users update and uninstall their own apps without an administrator.

### 4. Installing for the system: two steps, every time

Installing, updating or removing an app **for the whole system**, or
installing from an **Untrusted** source, needs:

1. **An administrator signs in** with their account (password or passkey,
   ADR 0168). No administrator session is kept open and no `sudo` delay is
   reused: sign-in is asked for each operation.
2. **A validation window** that shows the app (name, icon, version, size), its
   source and trust level, and the requested capabilities (dangerous ones
   highlighted, ADR 0049). The administrator approves or refuses.

The same OS window serves the Store and the terminal: `hackpkg install
--system …` opens it, and `sudo hackpkg` is no longer enough for these
operations. Files go to `/usr/lib/hackeros/apps/` as in ADR 0182.

### 5. The administrator block list

New file `/etc/hackeros/app-policy.config` (`system:system 0644`, editable by an
administrator with `sudo nano` or in Settings ▸ Apps):

```text
[blocked-apps]
org.example.chat=true

[blocked-categories]
social=true

[blocked-capabilities]
network.real.access=true
```

- An app blocked by its id, its category or a capability it requests can no
  longer be installed for yourself: it needs the §4 system install.
- Default values are set when the host is built (like sources, §1); the file
  is created at startup if it does not exist.
- The Store shows why an app needs an administrator.

### 6. `hackpkg`

```text
hackpkg install <appId | source>[@<version>] [--user | --system]
hackpkg remove <appId> [--user | --system]
hackpkg update [<appId>]
hackpkg list
hackpkg info <appId | source>
hackpkg sources [add <catalog | github:owner[/repo]> [--trusted] | remove <name>]
hackpkg policy
```

Without an option, `install` picks `--user` when allowed, otherwise it offers
the system install (§4). `--allow-untrusted` goes away: an untrusted source
always goes through the §4 window.

## Consequences

- A user without rights can add code to their own session; since this code
  shares the OS runtime and origin, it could in theory read the data of other
  accounts in the same browser. Trust in the source (reviewed and built by us
  for the official catalog) and the block list stand in for isolation.
- The app catalog is built when the session opens, with system apps then the
  user's apps.
- Two steps every time are slower for an administrator, in exchange for an
  explicit validation of each system install.
- Existing scripts using `sudo hackpkg` must switch to `hackpkg --system`.

## Benoit's answers

- **Q4** All of our catalog trusted; another catalog or a single repository
  added by an administrator; only sources built into the OS are trusted with
  no change by an administrator (Benoit's answer).
- **Q5** Per-user install without an administrator from a trusted source,
  unless blocked (set at build, editable by an administrator); system install
  validated in two steps, every time (Benoit's answer).
- **Q24** B: keep `github.com/Kazo-ca` trusted in addition to the official
  catalog, and an administrator can add a trusted source without redeploying.
- **Q25** A: the administrator chooses "trusted" when adding, not by default.
  **Q26** A: block list by app, category and capability. **Q27** A: one copy at
  a time. **Q28** A: the same window for `hackpkg`, with no `sudo` delay.

## Tasks

- [ ] `app-sources.config`: `[catalogs]` and `[updates]` sections, defaults set
      when the host is built, sources added by an administrator.
- [ ] `app-policy.config` (reading, defaults, creation at startup).
- [ ] Per-user install (`~/.local/share/hackeros/apps`,
      `installed-apps.config`), catalog built when the session opens.
- [ ] Two-step validation window, shared by the Store and `hackpkg`.
- [ ] `hackpkg`: `--user`, `--system`, `sources add/remove`, `policy`.
- [ ] Settings ▸ Apps ▸ Sources and block list.
- [ ] Documentation: `app-installation.md`, `linux-commands.md`.
