# Per-user file associations (API and formats)

This document describes what an app or a third party sees of file associations: the format of the two files, the resolution order, the `HackApps` API and the capabilities. The implementation (layers, index, Settings panel, "Open with", `xdg-mime`, tests) is in [`../dev/user-file-associations.md`](../dev/user-file-associations.md).

## Files and format

| Where | File | Who writes it |
|---|---|---|
| Machine | `/etc/hackeros/file-associations.json` | an administrator (`sudo nano`) |
| User | `~/.config/hackeros/file-associations.json` | the user: Settings, "Open with", `xdg-mime default`, SDK, `nano` |

Both files have the same schema:

```json
{"schemaVersion":1,"associations":[
  {"extension":".md","appId":"org.hackeros.code-editor","actions":["open"]},
  {"mediaType":"x-scheme-handler/https","appId":"com.example.browser","actions":["open"]}
]}
```
## Resolution order

1. The app requested by the intent (`PreferredAppId`).
2. The choice of **the user who runs the intent** (the principal; `sudo -u
   bob xdg-open` reads `bob`'s file).
3. The machine default.
4. The only app that declares the type, otherwise the picker (or code 4 of
   `xdg-open` in a terminal).

An entry that names an unknown or uninstalled app is skipped: a stale choice
falls back to the machine default. An invalid user file is ignored as a whole
and never rewritten. `open` and `edit` are chosen separately. The order applies
to `OpenFile`, `EditFile`, `OpenUri`, `RevealFile` (`inode/directory`) and
"Open with" (the default first).
## SDK API

| Member | Role | Capabilities |
|---|---|---|
| `HackApps.GetDefaultHandlersAsync()` | `HackFileAssociation(Target, Action, AppId, Layer)` rows: the user's, then the machine's. The first row for a target and an action is the one used. | `file-associations.read` |
| `HackApps.SetDefaultHandlerAsync(target, appId, action = "open")` | Writes the user's choice. The app must declare the target and the action. | `file-associations.read`, `filesystem.user-home.write` |
| `HackApps.ResetDefaultHandlerAsync(target, action = "open")` | Removes the user's choice. | same |

`HackDefaultHandlerStatus`: `Changed`, `InvalidTarget`, `InvalidAction`,
`AppNotFound`, `AppCannotHandle`, `FileInvalid`, `WriteFailed`. A target is
`.ext` or `type/subtype`, lowercased. A gateway older than
ADR 0121 throws `NotSupportedException`.

```csharp
HackDefaultHandlerStatus status = await HackApps.SetDefaultHandlerAsync(".md", "org.hackeros.code-editor");
await HackApps.SetDefaultHandlerAsync("x-scheme-handler/https", "com.example.browser");
await HackApps.ResetDefaultHandlerAsync(".md");
```

## Capabilities

- No new capability: writing one's own file requires
  `filesystem.user-home.write`, which the app could do anyway.
