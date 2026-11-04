# Kazo.Os.Theming.Blazor

Native Blazor theme composition for Kazo OS and for the reusable window/taskbar packages. The
Razor class library owns the offline static asset `_content/Kazo.Os.Theming.Blazor/themes.css`,
the root `ThemeScope`, and the token-driven `ThemePreview` used by appearance selectors.

```razor
@using Kazo.Os.Theming.Abstractions
@using Kazo.Os.Theming.Blazor

<ThemeScope ThemeId="@WellKnownThemeIds.HackerOs"
            Platform="ThemePlatform.Desktop"
            AccentId="@WellKnownAccentIds.Green"
            AnimationsEnabled="true">
    @* Shell, WindowHost, and Taskbar content *@
</ThemeScope>
```

Render one `ThemeScope` around a shell root. All attribute values are validated against
`ThemeCatalog`; arbitrary CSS, remote fonts, images, scripts, and theme URLs are not accepted.

## Localization

The package's texts (accessible labels, the theme preview's name) live in `Resources/Strings.resx`
(English) and `Resources/Strings.fr.resx` (French), read through
`Kazo.Os.Theming.Blazor.Localization.PackageStringLocalizer` (ADR 0104): a host that
registers an `IStringLocalizer<T>` (Kazo OS registers its session-language
`SessionStringLocalizer<T>`) gets its language; a host without localization gets English.
