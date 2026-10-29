> [Version française](../../../../fr/wasm/dev/adr/0193-local-time-and-time-zone.md)

# ADR 0193: local time and time zone

## Status

Accepted on 2026-10-29. Benoit's decisions 3 and 4 of 2026-10-29 (thread "Improve
windows and taskbar"), work item 2 of issue
[#242](https://github.com/Kazo-ca/HackerOs/issues/242).

- **Completes:** [ADR 0104](0104-localization.md) (language and formats of the
  session), [ADR 0105](0105-accessibility-preferences.md) (same two-layer live
  setting model), [ADR 0056](0056-sdk-apis-for-system-apps.md) §8 (OS documents
  opened by Settings).
- **Does not change:** [ADR 0045](0045-logs-as-files.md) (log files stay in UTC,
  §6).

## Context

- The taskbar clock shows `ISimulationClock.UtcNow` without conversion: it is UTC
  time. So does the calendar of the clock panel.
- The OS has no time zone. Elsewhere, the code mixes `TimeZoneInfo.Local`
  (`dmesg`, trash), `ToLocalTime()` (File Explorer, Settings, Code Editor) and UTC
  (`ls -l`).
- In the browser, .NET WebAssembly gives `TimeZoneInfo.Local` the browser's zone
  (`Intl.DateTimeFormat().resolvedOptions().timeZone`), with the IANA time zone
  data.
- Benoit decided: the browser's zone by default, a "Time zone" setting (Automatic
  or a choice from a list) in Settings ▸ Date and time, saved per user, and a
  single "local time" service for the whole OS, exposed in the SDK.

## Decision

### 1. The time zone of a session

The zone is an IANA identifier (`America/Toronto`, `Europe/Paris`, `UTC`) or
`auto`. `auto` means the device's zone, given by the browser
(`TimeZoneInfo.Local`). An unknown identifier counts as `auto`.

### 2. The setting: a two-layer `datetime.config`

The setting is the `timeZone` key of the `datetime` OS document, like
`accessibility.config`:

- `/etc/hackeros/datetime.config`: the machine's default, also used by the
  sign-in screen;
- `~/.config/hackeros/datetime.config`: the user's choice, which wins.

Without a file, the value is `auto`. The document is listed for
`HackSettings.OpenSystemAsync`, so Settings writes it with the public SDK.

### 3. The session service

`SessionTimeZoneService` (Platform Core) reads and watches the document. It gives
the effective zone and raises `Changed` when it changes. A change applies at once
(not at the next sign-in, as on GNOME): the clock, the calendar and the apps that
read the time again move to the new zone.

### 4. The SDK: `IAppExecutionContext.TimeZone` and `HackTimeZone`

- `IAppExecutionContext.TimeZone` gives the process's zone.
- The `TZ` environment variable of a process wins, as on Linux: `TZ=UTC ls -l` or
  `export TZ=Asia/Tokyo` in the terminal. The POSIX `:` prefix is accepted
  (`TZ=:Europe/Paris`).
- Without `TZ`, an OS process follows the session service (§3), so the change made
  in Settings. A process in a web worker receives the session's zone when it
  starts and keeps it.
- `HackTimeZone` (namespace `HackerOs.AppSdk.Globalization`) gives `Current`,
  `Now`, `ToLocal(...)`, `TryFind(...)`, `Resolve(...)`, `FormatOffset(...)` and the
  `Available` list of IANA zones. Apps no longer use `ToLocalTime()` or
  `TimeZoneInfo.Local`.

### 5. What shows local time

- the taskbar clock (on every screen) and the calendar of the clock panel;
- the new `date` command: `date` (local time), `date -u` (UTC), `date -I` (ISO
  8601 date), `date -R` (RFC 5322) and `date +FORMAT` (`%Y %m %d %H %M %S %Z %z
  %a %b %A %B %j %s %F %T %D %R %n %t %%`);
- `ls -l` (modification time) and `dmesg` (time of the lines);
- File Explorer (Properties), Settings (synchronization, passkeys), Code Editor
  (log) and the trash (`DeletionDate` of `.trashinfo`).

The platform's screens (server indicator, synchronization conflicts) use the
session service.

### 6. Logs stay in UTC

The `/var/log/*` files keep their UTC timestamps with `Z` (ADR 0045): programs
read them, and they must stay sortable and unambiguous, like the systemd journal,
which records in UTC. Display is local: `dmesg` converts each line to the
process's zone.

### 7. Settings ▸ Date and time

A "Date and time" page (Personalization group, `--page=datetime`) shows the local
time, the UTC time and the effective zone with its offset, then the choice:
"Automatic (time zone of this device: …)" or a zone from the
`HackTimeZone.Available` list, filtered by a search. The choice is written to the
user layer and applies at once.

## Consequences

- The clock shows the user's time. Dates are consistent across the whole OS.
- `ITaskbarStatusSource` gains `TimeZone` (UTC by default, so other hosts do not
  change); the platform sets it to the session's zone.
- SDK additions in `PublicAPI.Unshipped.txt` (next minor version):
  `IAppExecutionContext.TimeZone` (default member, so compatible) and
  `HackTimeZone`.
- Not done yet: `timedatectl`, a 12 h / 24 h setting separate from the language
  (the format stays the one of `LC_TIME`).

## Options set aside

- **The zone in the login environment only** (`TZ` read at sign-in from a file):
  simple and very Linux-like, but a change in Settings would only have reached the
  clock at the next sign-in.
- **Logs in local time**: readable by eye, but ambiguous when the clocks change
  and hard to sort across zones.
