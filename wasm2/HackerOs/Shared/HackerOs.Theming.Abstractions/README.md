# HackerOs.Theming.Abstractions

Framework-neutral contracts for the built-in Kazo OS theme system. The package exposes the
stable desktop/mobile theme IDs, the bounded accent IDs, `ThemeCatalog`, and the persisted
`ThemePreferences` value. It has no Blazor, browser, host, or server dependency.

```csharp
ThemeDefinition theme = ThemeCatalog.Get(WellKnownThemeIds.Windows7);
IReadOnlyList<ThemeDefinition> mobileThemes = ThemeCatalog.GetThemes(ThemePlatform.Mobile);
```

Theme IDs are exact, ordinal, and data-only. Consumers should validate persisted or external
values with `ThemeCatalog.TryGet` and `ThemeCatalog.IsKnownAccent` before rendering them.

User-visible texts (token labels and descriptions, validation reasons, theme file diagnostics)
are English in their properties and translated on demand from `Resources/Strings*.resx`
(ADR 0104): `ThemeTokenDefinition.GetLabel(culture)`, `GetDescription(culture)`,
`ThemeTokenValidator.TryValidate(type, value, culture, ...)` and
`ThemeFileDiagnostic.GetMessage(culture)` / `ToString(culture)`. Built-in theme names, token
names, ids and file contents are never translated.
