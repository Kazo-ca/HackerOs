> [Version française](../../../fr/wasm/dev/theme-editor.md)

# User themes and theme editor

This document covers the implementation of user themes and the theme editor. The format of the `.theme` file, the list of tokens and the message-reading API are in [`../framework/theme-files.md`](../framework/theme-files.md).

Work item `v3/11-apps`, [ADR 0103](adr/0103-hack-paint-image-files-and-theme-editor.md)
§2 (completes ADR 0041). Ported from `src/apps/theme-editor.ts`, without the
old app's free CSS (`customCss`).

## Purpose

The user creates their own themes: a `.theme` file that starts from a built-in
theme (`base`) and overrides a few validated CSS **tokens** (`--hos-*`). The
file is a real OS file: it is created with the theme editor, or with `nano`,
copied with `cp`, sent, opened through the explorer. Settings offers it next to
the built-in themes.

## Format and tokens

The format of the `.theme` file (sections, locations, tolerant reading, media) and the list of validated tokens are described in [`../framework/theme-files.md`](../framework/theme-files.md).

## Architecture

| Piece | Project | Role |
|---|---|---|
| `ThemeTokenCatalog`, `ThemeTokenValidator` | `Shared/HackerOs.Theming.Abstractions` | list and validation of tokens |
| `ThemeFile` | same | `Parse` (tolerant) and `Serialize` of the `.theme` format |
| `UserTheme`, `UserThemeLibrary` | same | theme read; reading of the two folders through delegates (`LoadAsync`), non-shadowing rules (`Build`) |
| `ResolvedTheme.Resolve` | same | theme actually drawn for an id and a form factor (desktop or mobile) |
| `ThemeScope`, `ThemePreview` (`Tokens` parameter) | `Platform/HackerOs.Theming.Blazor` | apply the tokens through `style.setProperty` on their root element, via `wwwroot/themeTokens.js` (only `--hos-*` properties are touched, and only the differences) |
| `UserThemeCatalogService` | `Platform/HackerOs.Platform.Core/Appearance` | the shell's live catalog: reads `/etc` as the system and `~` as the user, watches the two folders (`SettingsFilesWatcher.ForDirectories`), rereads on session change, logs each warning once |
| `App.razor` | `OS/HackerOs.Ecosystem` | `ThemeScope ThemeId=@ActiveTheme.BaseThemeId Tokens=@ActiveTheme.UserTheme?.Tokens` |
| Theme Editor | `Apps/System/HackerOs.Apps.ThemeEditor` | the app |
| Settings | `Apps/System/HackerOs.Apps.Settings` | user themes in the desktop and mobile lists, "Open the Theme Editor" button |

### Resolving an id

`appearance.config` keeps `desktopThemeId` and `mobileThemeId`; since v3/11
these are free strings (no longer an enumeration, `schemaVersion` stays 2).

1. A built-in theme of the right form factor: it applies.
2. A user theme offered for that form factor: its base theme applies, then its
   tokens on top. If its base targets the other form factor (a theme based on
   `hackeros` also offered on mobile), its tokens apply on the default theme of
   the active form factor.
3. Otherwise (unknown id, file deleted or rejected, wrong form factor): the
   form factor's default theme, as before.

## The theme editor

System app `org.hackeros.theme-editor`, on the public SDK only (ADR 0055).
Capabilities: home folder read and write, open and save dialogs, and
`filesystem.system.read` (dangerous, granted to system apps) to list
`/etc/hackeros/themes`. Window 1180 × 760, minimum 820 × 540 (ADR 0095).

- **Toolbar**: *New from* (base theme), *Open…*, *Save* (to
  `~/.config/hackeros/themes/<id>.theme`, folder created if needed), *Export…*
  (anywhere through the dialog), *Use on desktop* / *Use on mobile* (writes the
  id in the user layer of `appearance` through `HackSettings.OpenSystemAsync`).
- **Left column**: name, id (follows the name as long as it is not edited by
  hand), base theme, offered form factors, path; list of installed themes and
  warnings of their files.
- **Center**: one tab per token group (MudTabs), a typed field per token (color
  picker when the value is convertible), error under the field; Documentation
  tab.
- **Right**: live preview, desktop or mobile, in a nested `ThemeScope` (window,
  taskbar, buttons, fields) and a `ThemePreview` card.
- An invalid value stays displayed so that it can be corrected, but enters
  neither the preview nor the file, and blocks saving with the list of
  problems.
- Closing with unsaved changes asks for confirmation (`IWindowCloseGuard`);
  returning to the window rereads the theme list.
- A `.theme` opened through the explorer ("Open with") or passed as an argument
  at launch is loaded at startup.

### Languages (ADR 0104)

Toolbar, identity panel, tabs, preview, token fields, documentation and
messages come from `Resources/Strings.resx` / `Strings.fr.resx`.
`ThemeEditorDocument` receives the window's localizer (default name "My …",
problems that prevent saving). The documentation sentences contain `<code>` and
are rendered as markup from the assembly's resources (never from a file),
hence the `::deep code` of `ThemeTokenDocumentation.razor.css`.

The reading API of the `HackerOs.Theming.Abstractions` package (localized messages, token validation) is in [`../framework/theme-files.md`](../framework/theme-files.md).

## Key decisions

- Files are read and written through the app's file gateway
  (`IAppFileSystemGateway`), like the text editor: a file chosen in a dialog
  outside the home folder is accessible only through the token the dialog
  handed over, which `HackFile` cannot carry. This is C#; JS never touches files
  (ADR 0103 §1 and §2).
- Folder reading is shared through delegates (`UserThemeLibrary.LoadAsync`) so
  that the shell (OS gateways) and the apps (SDK) use the same rules without a
  new dependency.
- The shell service reads `~` with the user's permissions: an unreadable file
  (mode `0600` of another user) is simply ignored.

## Tasks

- [x] List of tokens, types and validation (`ThemeTokenCatalog`, `ThemeTokenValidator`).
- [x] `.theme` format: tolerant reading, writing, 64 KiB, diagnostics.
- [x] Library: `/etc` and `~`, no shadowing, resolution by form factor.
- [x] `ThemeScope`/`ThemePreview` apply tokens through `style.setProperty`.
- [x] `UserThemeCatalogService`: reading, folder watching, session change.
- [x] `appearance.config`: theme ids as free strings.
- [x] Theme Editor app: editing by groups, preview, documentation, save, export, use.
- [x] Settings: user themes listed, link to the editor.
- [x] Tests: format, rejected tokens, non-shadowing, resolution, theme applied
  after reload, editor model (`Platform.Core.Tests/Appearance`,
  `HackerOs.Apps.ThemeEditor.Tests`).
- [ ] Editor axe scans: work item `v3/13`.
