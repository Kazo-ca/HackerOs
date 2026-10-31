> [Version française](../../../../fr/wasm/dev/adr/0204-os-event-sounds.md)

# ADR 0204: OS event sounds

## Status

Accepted on 2026-10-30. Work item 9 of the Hack Store plan
([ADR 0190](0190-theme-packs.md) §4, point 3), `TODO` issue
[#256](https://github.com/Kazo-ca/HackerOs/issues/256), `todo/base` series.

- **Complements:** [ADR 0190](0190-theme-packs.md) (the `sounds/` folder of a
  `.hacktheme` pack targets this system), [ADR 0044](0044-settings-as-files.md)
  (two-layer settings files), [ADR 0056](0056-sdk-apis-for-system-apps.md) §8
  (OS documents opened by Settings).

## Context

- The OS played no sound. Benoit wants sounds for the OS events (notification,
  error, sign-in…), in OGG or MP3, a setting to turn them off and the choice of
  the sound theme in Settings.
- Store packs (ADR 0190) will carry sounds: the OS needs a sound theme format it
  reads from disk.
- On Linux, sound themes follow the freedesktop.org specification: one folder
  per theme in `/usr/share/sounds/` (or `~/.local/share/sounds/`), an
  `index.theme` file and one file per sound name (`dialog-error.oga`).
- A browser plays a sound only after a user gesture on the page (click, key).

## Decision

### 1. A sound theme is a freedesktop.org folder

```text
/usr/share/sounds/<id>/index.theme        [Sound Theme] Name=, Name[fr]=, Comment=, Directories=
/usr/share/sounds/<id>/stereo/<name>.oga  one file per event (.oga, .ogg or .mp3)
~/.local/share/sounds/<id>/...            the user's themes, same format
```

- `<id>`: lowercase letters, digits, `-`, `_`, `.` (64 characters at most).
- `Directories=` lists the sub-folders searched in order; the theme folder is
  searched last. A path with `/` or `..` is ignored.
- A file over **1 MB** is not played (limit of ADR 0190 §3).
- A system theme wins over a user theme of the same name: no masking, as in
  ADR 0103.

### 2. The events and their sound names

| Event | freedesktop.org sound name | Source |
| --- | --- | --- |
| Notification | `message-new-instant` | information or success notification |
| Warning | `dialog-warning` | warning notification, message box with the warning icon |
| Error | `dialog-error` | error notification, message box with the error icon |
| Sign-in | `desktop-login` | `SessionActivatedEvent` |
| Sign-out | `desktop-logout` | `SessionLoggedOutEvent` |

A sound missing from the chosen theme plays the `hackeros` theme's (like the
specification's `Inherits=`). The same event twice within 150 ms sounds once.
Only the session user's notifications sound.

### 3. The `sound.config` setting

Two-layer OS document, `/etc/hackeros/sound.config` (shipped by a fresh
installation) then `~/.config/hackeros/sound.config`:

| Key | Type | Default |
| --- | --- | --- |
| `enabled` | boolean | `true` |
| `theme` | theme identifier | `hackeros` |
| `volume` | integer 0 to 100 | `60` |

The sound service re-reads the file as soon as it changes. A theme or volume
change made during the session plays the notification sound, like GNOME's Sound
panel; opening another session does not.

### 4. Two built-in themes, installed as real files

`hackeros` ("HackerOS", console bleeps, the default) and `soft` ("Soft",
chimes) are computed by `build/tools/generate-system-sounds.py` (no sample from
elsewhere), encoded as OGG Vorbis (about 5 KB per sound), embedded in
`HackerOs.Platform.Core` and written by `FileSystemSeeder` to
`/usr/share/sounds/<id>/` (root, `0644`), like a distribution package. An
existing file is never replaced. `~/.local/share/sounds/` is created in every
home folder.

### 5. Playback

- `OsSoundService` (Platform Core, one instance per session) picks the file,
  reads its bytes with the user's rights (their home folder) or the system's,
  and hands them to the "sound card" `IOsSoundOutput`.
- `BrowserSoundOutput` (Platform Blazor) plays it with the Web Audio API
  (`osSounds.js`): decoded once, kept by the file's path and revision, played
  through a gain. Before the first gesture on the page, the sound is dropped
  without error.
- A sound never makes what it accompanies fail.

### 6. Settings ▸ Sound

Panel of the Personalization group, public SDK only: a "Play system sounds"
switch, the volume, the list of themes of `/usr/share/sounds` and
`~/.local/share/sounds`, and the table of files each event plays.

### 7. Out of this work item

- No SDK API for an app to play a system sound (question Q2 asked to Benoit,
  recommended answer: later, separate `TODO` issue).
- Installing the sounds of a `.hacktheme` pack (ADR 0190, issue #253).

## Consequences

- A pack's `sounds/` folder becomes a sound theme copied to
  `~/.local/share/sounds/<packId>/`, with no other system to build.
- About 50 KB of sounds are added to `HackerOs.Platform.Core` and to the
  simulated disk.
- A change to the built-in sounds does not reach an existing installation (the
  file is already there), as for the other seeded files.

## Questions asked to Benoit

- **Q1** Sounds on by default? A (recommended): yes, at 60 %; B: off.
- **Q2** May apps play the system sounds? A (recommended): later, separate
  `TODO` issue; B: SDK API in this work item.

The work item follows the recommendations until the answers come.

## Tasks

- [x] Sound theme format, event names (`OsSoundEvents`, `SoundThemeIndex`).
- [x] `sound.config` and `OsSoundService`.
- [x] Built-in `hackeros` and `soft` themes in `/usr/share/sounds`.
- [x] Web Audio playback (`BrowserSoundOutput`, `osSounds.js`).
- [x] Sounds of notifications, message boxes and sessions.
- [x] Settings ▸ Sound.
- [x] Tests, docs, `features.md`.
