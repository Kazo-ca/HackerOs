> [Version française](../../../../fr/wasm/dev/adr/0231-network-drives-count-as-personal-files.md)

# ADR 0231: the user's network drives count as their personal files

## Status

Accepted on 2026-11-06; Benoit chose the recommendation (question 2 of the
thread "Concevoir l'app Webamp avec Benoit", see "Questions asked to Benoit").
Issue [#381](https://github.com/Kazo-ca/Os/issues/381).

- **Replaces:** [ADR 0181](0181-host-folder-mounted-as-a-network-drive.md)
  §6 ("App access"), only for the drive of the acting user.
- **Does not change:** mounting, the `0700`/`0600` modes, the owner, or who
  mounts and unmounts (ADR 0181 §1 to §5 and §7 to §9); the classification of
  capabilities ([`capabilities.md`](../../framework/capabilities.md)).

## Context

- `FileSystemService.ResolveCapability` picks the capability an app needs from
  the path: `filesystem.private.*` for `~/.config/<appId>`,
  `filesystem.user-home.*` inside `/home/<user>`, and `filesystem.system.*`
  everywhere else. The `filesystem.system.*` capabilities are **dangerous**: an
  administrator approves them at install time.
- A network drive (ADR 0181) is mounted at `/mnt/<name>`, outside the home. An
  app that only has `filesystem.user-home.read` therefore cannot read a file of
  the drive, even when the user opens it themselves with a double-click, with
  "Open with" or with the OS open dialog.
- ADR 0181 §6 says that an app "goes through the open and save dialogs". In
  fact no dialog grants any right on the chosen file: `filesystem.user-selected.*`
  exists in the capability catalog but is neither granted nor checked anywhere.
  Chess cannot open a `.pgn` from a network drive, and the planned Webamp app
  could not play an `.mp3`.
- The drive belongs to the user who mounted it (mode `0700`): it is a folder of
  **their** computer, which they chose themselves in the browser's dialog. On
  Windows and macOS, a user's network drive is open to apps like their
  documents.

## Decision

1. **Capability.** A path inside a `hostfs` mount whose owner is the acting user
   requires `filesystem.user-home.read` to read and `filesystem.user-home.write`
   to write, like their home folder. `ResolveCapability` asks the mount router
   for the mount that contains the path, and compares its owner with the user of
   the operation.
2. **Permissions.** Permission bits and the owner still apply after the
   capability: nothing changes for other accounts (the drive stays `0700`).
3. **Outside the user's drive.** Another user's drive, `/mnt` itself and any
   other path outside the home stay under `filesystem.system.*`. The File
   Explorer, the commands and the OS dialogs keep their current capabilities.
4. **A disconnected drive** (browser permission not granted again yet) answers as
   today (`IsReady` false, provider error); the capability changes nothing there.
5. **No new API.** The change is in the OS check: an app on SDK 3.0 benefits from
   it as soon as the OS that runs it has it. On an older OS, a file of the drive
   still gives "Permission denied".
6. **`filesystem.user-selected.*`** stays as it is: a per-file right for a chosen
   file is not decided here (it would not cover an `.m3u` playlist or "Add
   folder", which read other files of the drive).

## Questions asked to Benoit

Benoit's answer (2026-11-06): "ok", the recommendation (A).

2. Apps cannot read `/mnt` today. How do we fix it?
   - A) In the OS: the user's network drives count as their personal files (same
     capability as `/home`). **(Recommended, chosen)**
   - B) In the OS: a file the user opens themselves becomes readable by the app,
     that file only (an `.m3u` list or "Add folder" on `/mnt` would not work).
   - C) The app declares `filesystem.system.read` (administrator approval at
     install time, reads the whole system).

## Consequences

- An app with `filesystem.user-home.read` reads the files of the user's network
  drives; with `.write`, it writes there. The text that describes
  `filesystem.user-home.*` at install time will have to say "your files,
  including your network drives".
- Webamp ([Os-App-Webamp](https://github.com/Kazo-ca/Os-App-Webamp)) and Chess
  open the files of a network drive without a dangerous capability.
- Planned tests: capability resolved for one's own drive, refusal for another
  user's drive and for `/mnt`, reading through `KazoFile` on a fake drive, same
  result in a Web Worker.
- Docs to update with the code: [`capabilities.md`](../../framework/capabilities.md),
  [`file-explorer.md`](../file-explorer.md) (network drives), `features.md`,
  `changes/unreleased` entry.
