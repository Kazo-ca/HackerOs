> [Version française](../../../../fr/wasm/dev/adr/0170-settings-side-menu.md)

# ADR 0170: collapsible side menu in Settings

## Status

Accepted on 2026-10-25 at Benoit's request ("on the settings page, I don't like
having tabs at the top to move between pages. I'd like a menu on the left that
can be collapsed or expanded"). Replaces the `MudTabs` tab navigation of the
Settings app. Does not change the content of the pages.

## Context

Settings has 15 to 17 pages (Backup depending on the host, Application for
`--app=<id>`). The `MudTabs` tabs at the top overflowed horizontally and had to
be scrolled; pages were identified by their index (`AppearanceTabIndex`,
`FixedTabCount`, "Application" kept last so the other indexes would not shift).

## Decision

1. **Menu on the left.** `SettingsWindow` shows a `<nav>` on the left and the
   active page on the right. The menu is a vertical tab list (WAI-ARIA:
   `role="tablist"`, `aria-orientation="vertical"`, `role="tab"`, one
   `role="tabpanel"`): the up/down arrows, Home and End change page, and the E2E
   tests that look for `GetByRole(AriaRole.Tab, "…")` remain valid.
2. **Groups.** Pages are grouped: Personalization (Appearance, Desktop,
   Accessibility, Language), Applications (File associations, Granted
   capabilities, Permissions, Installed applications, OS components, File menus,
   Application), Network (Server connection, Synchronization), System (Users,
   Memory, Backup, About the system).
3. **Collapse and expand.** A button at the top of the menu ("Collapse the
   menu" / "Expand the menu", `aria-expanded`) switches from a 14 rem menu with
   labels to a 3.25 rem icon bar; group titles become separators, and each icon
   keeps its name as a tooltip and accessible name. The state lives in the
   window: it is not saved.
4. **Narrow window and mobile.** Below 36 rem wide (container query on the
   window body, so also a small desktop window), the menu is always an icon
   bar; its button opens it over the page with a scrim, and choosing a page
   closes it.
5. **Pages by id.** Each page has a stable id (`appearance`, `desktop`,
   `accessibility`, `language`, `file-associations`, `capabilities`,
   `permissions`, `installed-apps`, `os-components`, `file-menus`, `server`,
   `sync`, `users`, `memory`, `backup`, `about`, `application`). `--page=<id>`
   now accepts every page except `application`, reserved for `--app=<id>`
   (ADR 0096 §4); an unknown id is ignored. `--page=appearance` and
   `--page=desktop` from ADR 0160 §8 keep their meaning.
6. The global `.hos-tab-header` rule in `css/app.css`, specific to the Settings
   tabs, is removed.

## Consequences

- A new page is added in `SettingsWindow.AllPages`
  (`SettingsWindow.Navigation.cs`: id, group, label, icon) and in the `@switch`
  of `SettingsWindow.razor`, with no index to recompute.
- The `Nav.*` strings exist in English and French; a test checks that every
  label and group of the menu is translated.
- Remembering the collapsed state from one opening to the next remains possible
  later (for example in `~/.config/hackeros/`), without changing this decision.
