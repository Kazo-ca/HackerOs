> [Version française](../../../fr/wasm/framework/time-zone.md)

# Local time and time zone

The OS keeps its time in UTC (the simulation clock, `IAppExecutionContext.Clock`)
and shows it in the user's time zone. Decision: [ADR 0193](../dev/adr/0193-local-time-and-time-zone.md).

## The setting

| Layer | File | Key |
| --- | --- | --- |
| Machine (also the sign-in screen) | `/etc/hackeros/datetime.config` | `timeZone` |
| User (wins) | `~/.config/hackeros/datetime.config` | `timeZone` |

`timeZone` is `auto` (the device's zone, given by the browser) or an IANA
identifier (`America/Toronto`, `Europe/Paris`, `UTC`). An unknown name counts as
`auto`. Settings ▸ Date and time (`--page=datetime`) writes the user layer; the
clock follows at once.

## The time zone of a process

1. the process's `TZ` variable, as on Linux (`TZ=Asia/Tokyo date`,
   `export TZ=UTC`, `:` prefix accepted);
2. otherwise the session's zone (`SessionTimeZoneService`), read at every access;
   a process in a web worker receives the one current when it started;
3. otherwise the device's zone.

## In an app

```csharp
using HackerOs.AppSdk.Globalization;

TimeZoneInfo zone = AppContext.TimeZone;                // or HackTimeZone.Current
DateTimeOffset now = HackTimeZone.Now;                  // the OS's time in that zone
string modified = HackTimeZone.ToLocal(entry.ModifiedAtUtc).ToString("g", AppContext.FormatCulture);
string offset = HackTimeZone.FormatOffset(zone, now);   // "UTC-04:00"
```

- Do not use `DateTimeOffset.ToLocalTime()` or `TimeZoneInfo.Local`: they ignore
  the user's choice.
- The format (12 h or 24 h, day/month order) comes from `FormatCulture`
  (`LC_TIME`, [`localization.md`](localization.md)).
- `HackTimeZone.Available` lists the IANA `Region/City` zones and `UTC`, sorted;
  `TryFind` and `Resolve` read a name; `GetId` gives the IANA identifier.
- No capability is needed.

## In the terminal

- `date`: local time (`Thu Oct 29 10:05:09 -04 2026`); `-u` UTC; `-I` ISO 8601
  date; `-R` RFC 5322; `+FORMAT` (`%F %T %Z %z %a %b %j %s`…).
- `ls -l` and `dmesg -T` show the time in the process's zone.
- The `/var/log/*` files stay in UTC (`2026-10-29T14:05:09.000Z`).

## Platform

- `SessionTimeZoneService` (Platform Core): `TimeZone`, `Setting`, `ToLocal`,
  `Changed`, initialized at startup with the accessibility preferences.
- `ITaskbarStatusSource.TimeZone`: the zone of the taskbar clock (UTC by default
  for a host that gives none).
