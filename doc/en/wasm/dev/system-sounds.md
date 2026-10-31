> [Version française](../../../fr/wasm/dev/system-sounds.md)

# System sounds

The OS plays a sound for its events: notification, warning, error, sign-in and
sign-out. Decision: [ADR 0204](adr/0204-os-event-sounds.md), issue
[#256](https://github.com/Kazo-ca/HackerOs/issues/256).

## For the user

- **Settings ▸ Sound**: a "Play system sounds" switch, the volume (0 to 100 %),
  the choice of sound theme. A new choice plays the theme's notification sound
  at once.
- The browser plays a sound only after a first click or key press on the page:
  a sound before that gesture (a guest session opened by itself) is dropped.
- On a new installation, the first sign-in sound plays a few seconds after the
  desktop appears, once the built-in themes are written.
- Built-in themes: **HackerOS** (`hackeros`, console bleeps, the default) and
  **Soft** (`soft`, chimes).

## The `sound.config` setting

`/etc/hackeros/sound.config` (machine, shipped by a fresh installation) then
`~/.config/hackeros/sound.config` (user):

```ini
enabled=true     # false turns every sound off
theme=hackeros   # a folder name of /usr/share/sounds or ~/.local/share/sounds
volume=60        # 0 to 100
```

An invalid value is the default; the volume is clamped to 0-100. An
administrator can lock a key as for the other OS documents (ADR 0044).

## Making a sound theme

A theme is a freedesktop.org folder, in `~/.local/share/sounds/<id>/` (for
yourself) or `/usr/share/sounds/<id>/` (for everyone, administrator):

```text
~/.local/share/sounds/retro/
├── index.theme
└── stereo/
    ├── message-new-instant.ogg   notification
    ├── dialog-warning.ogg        warning
    ├── dialog-error.mp3          error
    ├── desktop-login.oga         sign-in
    └── desktop-logout.oga        sign-out
```

```ini
[Sound Theme]
Name=Retro
Name[fr]=Rétro
Comment=Old beeps
Comment[fr]=Vieux bips
Directories=stereo
```

- `<id>`: lowercase letters, digits, `-`, `_`, `.`.
- Formats: `.oga`, `.ogg` (Ogg Vorbis or Opus) and `.mp3`, **1 MB** at most per
  file. A bigger file is ignored.
- A missing sound is taken from the `hackeros` theme.
- A user folder named like a system theme is ignored.

At the terminal:

```sh
mkdir -p ~/.local/share/sounds/retro
cp -r /usr/share/sounds/soft/* ~/.local/share/sounds/retro/
nano ~/.local/share/sounds/retro/index.theme
```

## Architecture

| Piece | Project | Role |
| --- | --- | --- |
| `OsSoundEvent`, `OsSoundEvents`, `SoundThemeIndex` | Simulation Abstractions | Events, sound names, paths, 1 MB limit, reading `index.theme`. |
| `SoundSettingsDocuments` | Platform Core | The `sound.config` document (schema, defaults, `SoundPreferences`). |
| `BuiltInSoundThemes` | Platform Core | Built-in themes, embedded (`Sounds/Themes/<id>/*.oga`), written to `/usr/share/sounds` by `FileSystemSeeder.InstallSoundThemesAsync`, which `OsSoundService` runs once in the background after the first sign-in (not during the login, which it made 2 s slower). `/var/lib/hackeros/installed/sound-themes.list` records the installed files, like dpkg's `.list` files: a boot whose list matches skips the install. |
| `OsSoundService` | Platform Core | Reads and watches `sound.config`, listens to notifications (`INotificationFeed`) and sessions (`IEventBus`), picks the file, plays through `IOsSoundOutput`. |
| `IOsSoundOutput` / `BrowserSoundOutput` | Platform Core / Platform Blazor | "Sound card": Web Audio in `wwwroot/osSounds.js`, decoded buffers kept by path and revision. |
| `DialogCoordinator` | Platform Blazor | Plays warning or error for a message box with that icon. |
| `SoundPanel` | Settings app | The Settings ▸ Sound page, public SDK only. |

Search order of a sound: `/usr/share/sounds/<theme>`,
`/usr/share/hackeros/theme-packs/<theme>/sounds`, `~/.local/share/sounds/<theme>`,
then `~/.local/share/hackeros/theme-packs/<theme>/sounds` (the sound theme of a
Hack Store pack has the pack's id, [ADR 0213](adr/0213-theme-packs-build.md)); in the folder found, each folder of
`Directories=` then the folder itself, for `<name>.oga`, `.ogg`, `.mp3`;
otherwise the `hackeros` theme. The same event twice within 150 ms sounds once.

## Rebuilding the built-in sounds

The sounds are computed, with no sample from elsewhere:

```sh
cd wasm2/HackerOs
python3 build/tools/generate-system-sounds.py /tmp/sounds
for f in /tmp/sounds/*/*.wav; do ffmpeg -y -i "$f" -c:a libvorbis -q:a 2 -ar 22050 -ac 1 "${f%.wav}.oga"; done
cp /tmp/sounds/hackeros/*.oga Platform/HackerOs.Platform.Core/Sounds/Themes/hackeros/
cp /tmp/sounds/soft/*.oga Platform/HackerOs.Platform.Core/Sounds/Themes/soft/
```

An existing installation keeps its old files (`FileSystemSeeder` overwrites
nothing).

## Tests

`Tests/HackerOs.Platform.Core.Tests/Sounds/`: `index.theme` format and names,
installed files, sign-in sound, sounds off, notification severity, user theme
and fallback, 1 MB limit, no masking, theme change, repeat guard, clamped
volume.

## Tasks

See [ADR 0204](adr/0204-os-event-sounds.md) §Tasks.
