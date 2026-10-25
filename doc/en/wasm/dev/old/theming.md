# Shared Theme System

## Purpose

HackerOS themes skin the operating-system surface — desktop or mobile shell,
boot/authentication surfaces, window chrome, taskbar, launcher, and exported
window/taskbar packages — from one bounded catalog and one static CSS asset.
First-party apps inherit the same semantic tokens; app-specific editors and
canvases may retain their own deliberately authored work-surface palette.
The system restores the saved choice during host startup; opening Settings is
not required to apply it.

The built-in catalog contains:

| Form factor | Theme ID | Display name | User accent |
| --- | --- | --- | --- |
| Desktop | `hackeros` | HackerOS | Green, cyan, or purple |
| Desktop | `windows-98` | Windows 98 | Fixed by theme |
| Desktop | `windows-xp` | Windows XP | Fixed by theme |
| Desktop | `windows-7` | Windows 7 | Fixed by theme |
| Desktop | `windows-10` | Windows 10 | Fixed by theme |
| Desktop | `macos` | macOS | Fixed by theme |
| Desktop | `ubuntu` | Ubuntu | Fixed by theme |
| Mobile | `android` | Android | Fixed by theme |
| Mobile | `ios` | iOS | Fixed by theme |

These are inspired visual treatments, not copies of proprietary assets. They
use system font fallbacks, CSS gradients, borders, radii, shadows, and the
shared icon library; no vendor bitmap or remote font is shipped.

## Architecture

```text
HackerOs.Theming.Abstractions        (pure .NET catalog and preferences)
             │
             ├── HackerOs.Platform.Core       (settings codec/read projection)
             │
             └── HackerOs.Theming.Blazor      (ThemeScope, preview, themes.css)
                         │
                         ├── HackerOs.Windowing.Blazor
                         ├── HackerOs.Taskbar.Blazor
                         ├── HackerOs.MobileShell.Blazor
                         ├── HackerOs.Platform.Blazor
                         └── HackerOs.Ecosystem / Settings
```

`HackerOs.Theming.Abstractions` is safe for non-UI projects. It exposes
`ThemeDefinition`, `ThemePlatform`, `ThemeCatalog`, `ThemePreferences`,
`WellKnownThemeIds`, and `WellKnownAccentIds`. Theme IDs are exact,
case-sensitive, stable persistence keys.

`HackerOs.Theming.Blazor` is the only owner of the complete visual theme
definitions. Its `wwwroot/themes.css` is published by the Razor class library
at `_content/HackerOs.Theming.Blazor/themes.css`. `ThemeScope` loads that asset
and emits only validated `data-*` selectors on one ancestor; semantic custom
properties then inherit through scoped Razor CSS and across project boundaries.
It does not append one stylesheet per window.

The Ecosystem root wraps all boot, authentication, desktop, mobile, and recovery
surfaces in one `ThemeScope`. It chooses `DesktopThemeId` or `MobileThemeId`
according to `UiPlatformPreferenceService`. `ThemePreferenceService` reads the
canonical setting at boot and raises `Changed` after an authorized Settings
write or sync refresh. Component libraries consume CSS variables only and do
not know about persistence.

This boundary is recorded by
[ADR 0041](../adr/0041-shared-theme-catalog-and-scoped-tokens.md).

## Applying a theme

Reference the two projects needed by the layer. Pure C# consumers require only
the abstractions project; a Razor surface uses the Blazor project (which already
references the abstractions project):

```xml
<ProjectReference Include="..\HackerOs.Theming.Blazor\HackerOs.Theming.Blazor.csproj" />
```

Wrap the highest shared ancestor, not every child component:

```razor
@using HackerOs.Theming.Abstractions
@using HackerOs.Theming.Blazor

<ThemeScope ThemeId="@WellKnownThemeIds.Windows7"
            Platform="@ThemePlatform.Desktop"
            AccentId="@WellKnownAccentIds.Green"
            AnimationsEnabled="true">
    <DesktopArea />
</ThemeScope>
```

`ThemeScope` rejects an unknown theme/accent combination instead of emitting
untrusted selector text. Only HackerOS declares `SupportsAccentColor`; other
themes keep their authored accent. The user's `prefers-reduced-motion` browser
setting overrides enabled motion declaratively.

Standalone consumers of `HackerOs.Windowing.Blazor` or
`HackerOs.Taskbar.Blazor` use the same wrapper. They must not copy `themes.css`
into their host and must not add a manual `<script>` or a second host-owned
theme stylesheet. The normal Razor static-asset pipeline supplies the RCL
asset, including in the PWA, debug Blazor host, and future server UI host.

## Persistence contract

Appearance is the sync-eligible OS settings document
`/etc/hackeros/appearance.json`. Schema version 2 is:

```json
{
  "schemaVersion": 2,
  "desktopThemeId": "hackeros",
  "mobileThemeId": "android",
  "accent": "green",
  "animationsEnabled": true
}
```

Desktop and mobile choices are independent so switching form factors does not
discard either choice. `AppearanceSettingsCodec` still accepts schema version
1 (`accent` plus `animationsEnabled`) and supplies the default HackerOS/Android
theme IDs in memory. Every new write is canonical version 2 JSON.

Only an authorized settings gateway writes this document. The shell's
`ThemePreferenceService` is a privileged read projection; it cannot silently
change user preferences.

## Semantic token contract

Layout CSS uses semantic `--hos-*` properties. Theme definitions assign values;
components must not branch on a theme ID when a token can express the
difference. The base selector in `themes.css` defines a safe value for every
token, and theme selectors override the relevant groups:

| Group | Representative tokens |
| --- | --- |
| Core surfaces | `--hos-bg-base`, `--hos-bg-surface`, `--hos-bg-elevated`, `--hos-bg-overlay` |
| Text and state | `--hos-text-primary`, `--hos-text-secondary`, `--hos-text-muted`, `--hos-text-inverse`, `--hos-error`, `--hos-warning`, `--hos-success`, `--hos-info` |
| Accent/focus | `--hos-accent`, `--hos-accent-hover`, `--hos-accent-subtle`, `--hos-accent-glow`, `--hos-focus-ring` |
| Shape/motion | `--hos-radius-*`, `--hos-shadow-*`, `--hos-transition-fast`, `--hos-transition-normal` |
| Buttons & controls | `--hos-btn-bg`, `--hos-btn-color`, `--hos-btn-border`, `--hos-btn-radius`, `--hos-btn-shadow`, `--hos-btn-hover-*`, `--hos-btn-active-*`, `--hos-btn-primary-*`, `--hos-btn-danger-*` |
| Inputs & forms | `--hos-input-bg`, `--hos-input-color`, `--hos-input-border`, `--hos-input-radius`, `--hos-input-shadow`, `--hos-input-focus-border`, `--hos-input-focus-ring` |
| Toolbars & cards | `--hos-toolbar-bg`, `--hos-toolbar-border`, `--hos-card-bg`, `--hos-card-border`, `--hos-card-radius`, `--hos-card-shadow`, `--hos-badge-*` |
| Tables & lists | `--hos-table-header-bg`, `--hos-table-header-color`, `--hos-table-border`, `--hos-table-row-hover-bg`, `--hos-table-row-selected-bg`, `--hos-table-row-selected-color` |
| Desktop/window | `--hos-desktop-background`, `--hos-window-*`, `--hos-titlebar-*`, `--hos-window-control-*` |
| Taskbar/launcher | `--hos-taskbar-*`, `--hos-taskbar-button-*`, `--hos-launcher-*` |
| Mobile shell | `--hos-mobile-surface-background`, `--hos-mobile-nav-*`, `--hos-mobile-shade-*` |

Compatibility aliases for older `--hackeros-*` and transitional `--hos-*`
names live in the shared base selector. New code uses the semantic names above;
do not add another alias dialect in an app.

Theme-specific structure is also expressed as bounded values. For example,
macOS changes window identity/action ordering and control order through
`--hos-window-identity-order`, `--hos-window-actions-order`, and
`--hos-window-*-order`; it does not query or rearrange the DOM with JavaScript.

## Creating a new built-in theme

Follow every step; a catalog-only entry or CSS-only selector is incomplete.

1. Choose a stable, lowercase kebab-case identifier and decide whether the
   theme is Desktop or Mobile. IDs are persisted API: do not rename one without
   a documented settings migration.
2. Add the constant to `WellKnownThemeIds`, then add one `ThemeDefinition` to
   the correct ordered list in `ThemeCatalog`. Set `supportsAccentColor: true`
   only when all supported accents have deliberately authored contrast states.
3. Add a static selector to
   `Platform/HackerOs.Theming.Blazor/wwwroot/themes.css` using both supported
   forms:

   ```css
   :where([data-theme="my-theme"], [data-hos-theme="my-theme"]) {
       --hos-color-scheme: dark;
       --hos-bg-base: #101214;
       --hos-bg-surface: #181b1f;
       --hos-text-primary: #f4f6f8;
       --hos-text-secondary: #b5bdc6;
       --hos-border: #46515c;
       --hos-accent: #62b4ff;
       --hos-focus-ring: #62b4ff;
       /* Override window/taskbar/mobile groups needed for a distinct treatment. */
   }
   ```

4. Exercise the theme through `ThemePreview` and the real Settings picker.
   Verify normal, hover, focus, active, disabled, error, focused/unfocused
   window, launcher, and mobile navigation states. A desktop theme must be
   checked with windows and taskbar; a mobile theme with the mobile shell.
5. Add/update catalog and validation tests. Check WCAG 2.2 AA contrast, keyboard
   focus visibility, 200% zoom, narrow viewport behavior, and both the explicit
   animation toggle and `prefers-reduced-motion`.
6. Build/publish the Ecosystem host and verify the asset is present at the RCL
   `_content` path and in the service-worker manifest. Do not create a fallback
   copy in `wwwroot` to make a broken route appear to work; see
   [`common-pitfalls.md`](common-pitfalls.md).
7. Update the built-in matrix above and any release/migration status that names
   the complete catalog.

Theme definitions are reviewed static data. Arbitrary CSS pasted by users,
JavaScript, dynamic code, remote fonts, remote images, and executable bundles
are prohibited by design-system decision D-013. If future downloadable themes
are introduced, they require a separate ADR and a validated declarative schema;
the current API must not be widened to execute legacy `customCss`.

## Key decisions

- One catalog and one RCL stylesheet serve both the OS and reusable chrome
  projects; there is no dependency from browser-independent windowing core to
  Blazor.
- Theme selection belongs to the composition root; leaf controls only consume
  inherited tokens.
- HackerOS alone exposes an independent accent selector. Historical OS themes
  remain visually coherent through their authored colors.
- Static tokens replace the legacy arbitrary-CSS injection mechanism.
- The persisted schema accepts the previous version but never writes it.

## Completed task list

- [x] Audit legacy themes, selectors, persistence, and unsafe custom CSS.
- [x] Define a framework-neutral nine-theme catalog and appearance schema v2.
- [x] Create the shared Razor theme project and static token asset.
- [x] Apply one startup-owned scope and connect the Settings picker.
- [x] Tokenize desktop, window, taskbar, launcher, and mobile surfaces.
- [x] Add tests and validate all hosts/static assets.
