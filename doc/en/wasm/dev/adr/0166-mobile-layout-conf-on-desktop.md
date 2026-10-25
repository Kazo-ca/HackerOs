> [Version française](../../../../fr/wasm/dev/adr/0166-mobile-layout-conf-on-desktop.md)

# ADR 0166: mobile layout as a conf file under Desktop

## Status

Accepted on 2026-10-23 at Benoit's request. Replaces the path and the JSON
format of [ADR 0165](0165-mobile-home-drawer-and-mouse-controls.md); the other
decisions of that ADR remain applicable.

## Decision

- The layout is `~/Desktop/.mobile/layout.conf`, specific to each user.
- The file is text following the OS settings grammar: `#` comments,
  key `version=1`, section `[dock]` with `apps=`, section `[pages]` with
  contiguous numeric keys from `1` to N. Each value is an ordered list
  of comma-separated IDs; an empty list is allowed. This HackerOS-specific
  format is inspired by Linux configuration files.
- `ConfigDocumentFormat` reads the existing grammar; the home screen's limits and
  validations remain identical. Writes normalize the text.
- `OsSettingsLocation.UserHome` adds a home-folder-relative resolution
  to `OsSettingsFile`, with the existing user access and permissions.
- If the destination is absent, a valid old JSON layout is read,
  then saved as conf. The source file stays intact. An existing destination,
  valid or not, prevents the migration. No new JSON save.
- Names starting with a dot are filtered on the desktop. The explorer
  still lets users reveal them with its hidden files command.

## Verification

Tests of format, limits, order, one-time migration and priority of the conf file
in `MobileHomeLayoutTests`; persistence and visibility in `MobileHomeScreenTests`.
Format and example: [mobile-home-screen.md](../mobile-home-screen.md).
