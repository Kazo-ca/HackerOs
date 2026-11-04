> [Version française](../../../fr/wasm/framework/theme-files.md)

# `.theme` theme files

This document describes the `.theme` file format, the token list and the theme-reading API for theme authors and tools. The theme editor, the resolution of a theme identifier and the implementation are in [`../dev/theme-editor.md`](../dev/theme-editor.md).

## `.theme` file

Settings format (ADR 0044), two sections:

```ini
# Kazo OS theme (ADR 0103).
[theme]
id = neon-crypt
name = Neon Crypt
base = kazoos
platforms = desktop, mobile

[tokens]
--kos-bg-base = #0b0f0b
--kos-accent = #39ff14
--kos-font-mono = "JetBrains Mono", monospace
```

- `id`: 1 to 64 lowercase letters, digits and hyphens; it is also the file name
  and the value written in `appearance.config`.
- `name`: 1 to 80 characters. `base`: a theme from the catalog (`ThemeCatalog`).
- `platforms`: `desktop`, `mobile` or both; when absent, that of the base theme.
- Maximum size: 64 KiB.
- **Tolerant reading** (ADR 0044 §5): an unknown token, an invalid value or
  a malformed line is ignored with a warning, the rest applies.
  Only an invalid identity (`id`, `name`, `base`) rejects the whole theme.
- Media type `text/x-kazoos-theme`: the explorer opens a `.theme` in
  the theme editor.

### Locations

| Folder | Read as | For |
|---|---|---|
| `/etc/kazoos/themes/` | the system | the administrator, for the whole machine |
| `~/.config/kazoos/themes/` | the session user (the mode bits decide) | the user |
| `/usr/share/kazoos/theme-packs/<pack>/themes/` | the system | Kazo OS Store packs installed for everyone ([ADR 0213](../dev/adr/0213-theme-packs-build.md)) |
| `~/.local/share/kazoos/theme-packs/<pack>/themes/` | the session user | packs installed for oneself |

No masking: a file whose `id` reuses a built-in theme, or an `id`
already taken by another file (the one in `/etc` wins), is refused with an
error in the log.

## Tokens

`ThemeTokenCatalog` (`Kazo.Os.Theming.Abstractions`) lists the 68 editable
tokens, each with a type, a group, a label, a description and an
example. The **Documentation** tab of the editor is generated from this list.

| Type | Accepted values (`ThemeTokenValidator`) |
|---|---|
| Color | `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, `rgb()`/`rgba()`/`hsl()`/`hsla()` with numbers and percentages only, CSS color name |
| Length | number (possibly negative) with `px`, `rem`, `em`, `%`, `vh`, `vw`, or `0` |
| Duration | `150ms`, `0.2s`, optionally followed by one of `ease`, `linear`, `ease-in`, `ease-out`, `ease-in-out` |
| Font family | names separated by commas (quoted or not) |
| Number | integer or decimal |

Everything else is refused, in particular `url(`, `var(`, `calc(`,
`expression(`, `;`, `{`, `}`: a theme contains only data (ADR 0103
§2.4). A value is at most 200 characters long.

| Group | Tokens |
|---|---|
| Colors | `--kos-bg-base`, `-surface`, `-elevated`, `-overlay`; `--kos-text-primary`, `-secondary`, `-muted`, `-inverse`; `--kos-border-subtle`, `--kos-border`, `--kos-border-strong`, `--kos-focus-ring`; `--kos-accent`, `-hover`, `-subtle`, `-glow`; `--kos-success`, `--kos-warning`, `--kos-error`, `--kos-info`; `--kos-desktop-background`, `--kos-desktop-grid-opacity` |
| Typography | `--kos-font-ui`, `--kos-font-mono`, `--kos-titlebar-font-size`, `--kos-titlebar-font-weight`, `--kos-taskbar-font-size`, `--kos-btn-font-weight` |
| Spacing | `--kos-radius-sm`, `-md`, `-lg`, `--kos-btn-radius`, `--kos-input-radius`, `--kos-control-height` |
| Windows | `--kos-window-background`, `-content-background`, `-text`, `-focused-border`, `-radius`; `--kos-titlebar-height`, `-background`, `-focused-background`, `-text`, `-focused-text`; `--kos-window-control-size`, `-radius`, `-background`, `-hover-background`, `-color`; `--kos-window-close-background`, `-hover-background`, `-color` |
| Taskbar | `--kos-taskbar-height`, `-gap`, `-background`, `-button-background`, `-button-hover-background`, `-button-active-background`, `-button-color`, `-button-active-color`, `-button-radius`, `-active-indicator`, `-launcher-background`, `-launcher-color`; `--kos-launcher-background`, `--kos-launcher-sidebar-background` |
| Animations | `--kos-transition-fast`, `--kos-transition-normal` (ignored when animations are turned off) |

The colors `accent`, `text-primary`, `text-secondary`, `text-muted`,
`border`, `success`, `warning`, `error` and `info` also receive their
computed `--kos-…-rgb` companion ("r, g, b"), used by styles that
write `rgba(var(--kos-accent-rgb), .2)`.

## Reading API

- `ThemeFile.Parse(text, origin, path)` reads a `.theme` file tolerantly and returns `ThemeFileParseResult` (theme and diagnostics); `ThemeFile.Serialize(UserTheme)` writes it.
- `ThemeFile.IsValidId`, `ThemeFile.Extension` (`.theme`), `ThemeFile.MediaType` (`text/x-kazoos-theme`), `ThemeFile.MaxFileBytes` (64 KiB).
- `ThemeTokenCatalog` lists the tokens; `ThemeTokenValidator.TryValidate(type, value, culture, out normalized, out error)` checks a value.
- `UserThemeLibrary.LoadAsync` reads the theme folders through delegates.

## Localized messages of the Theming.Abstractions package

`Kazo.Os.Theming.Abstractions` has its own `Resources/Strings.resx` /
`Strings.fr.resx`, read by a plain `ResourceManager` (the package stays free of
Kazo OS dependencies) with the culture the caller passes: an app gives
`AppContext.Culture` (the editor takes it from its localizer, `Strings.CultureOf(L)`),
the platform `SessionLocaleService.Culture`, and `null` gives English.
The `Label`, `Description` and `Message` properties stay in English; the
translations go through:

- `ThemeTokenDefinition.GetLabel(culture)` / `GetDescription(culture)` (key
  `Token.BgBase.Label` for `--kos-bg-base`);
- `ThemeTokenValidator.TryValidate(type, value, culture, out normalized, out error)`;
- `ThemeFileDiagnostic.GetMessage(culture)` and `ToString(culture)`
  (`path:line: warning: …`). A diagnostic produced by `ThemeFile` or
  `UserThemeLibrary` keeps its key and its arguments, so it reads in the
  language of whoever displays it. `ToString()` stays in English: it is the
  form written to the logs by `UserThemeCatalogService`.

Never translated: the names of the built-in themes (`ThemeCatalog`,
proper names: Kazo OS, Windows 98, macOS...), token names, ids
and the content of a `.theme` file (keys, values, `platforms = desktop, mobile`,
header comments written by `Serialize`).
