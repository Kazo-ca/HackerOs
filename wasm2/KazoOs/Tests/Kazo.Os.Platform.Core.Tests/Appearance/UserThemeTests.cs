using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Appearance;

/// <summary>The <c>.theme</c> format, token validation and theme resolution of ADR 0103 §2.</summary>
public sealed class UserThemeTests
{
    private const string Neon = """
        # a comment
        [theme]
        id = neon
        name = Neon Night
        base = kazoos
        platforms = desktop, mobile

        [tokens]
        --hos-accent = #ff00ff
        --hos-bg-base = rgb(5 5 10)
        --hos-radius-md = 6px
        --hos-transition-fast = 90ms ease-out
        """;

    [Fact]
    public void Parse_reads_identity_platforms_and_tokens()
    {
        ThemeFileParseResult result = ThemeFile.Parse(Neon, ThemeOrigin.User, "/home/alice/.config/kazoos/themes/neon.theme");

        UserTheme theme = Assert.IsType<UserTheme>(result.Theme);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("neon", theme.Id);
        Assert.Equal("Neon Night", theme.Name);
        Assert.Equal(WellKnownThemeIds.KazoOs, theme.BaseThemeId);
        Assert.Equal([ThemePlatform.Desktop, ThemePlatform.Mobile], theme.Platforms);
        Assert.Equal("#ff00ff", theme.Tokens.Single(pair => pair.Key == "--hos-accent").Value);
    }

    [Theory]
    [InlineData("--hos-accent = red; background: url(http://x)")]
    [InlineData("--hos-accent = expression(alert(1))")]
    [InlineData("--hos-not-a-token = #fff")]
    [InlineData("color = #fff")]
    [InlineData("not a key value line")]
    public void Parse_ignores_an_invalid_token_line_and_keeps_the_rest(string badLine)
    {
        ThemeFileParseResult result = ThemeFile.Parse(Neon + "\n" + badLine + "\n");

        UserTheme theme = Assert.IsType<UserTheme>(result.Theme);
        Assert.Equal(4, theme.Tokens.Count);
        ThemeFileDiagnostic warning = Assert.Single(result.Diagnostics);
        Assert.False(warning.IsError);
        Assert.Equal(13, warning.Line);
    }

    [Theory]
    [InlineData("id = neon", "id = Neon!")]
    [InlineData("base = kazoos", "base = solarized")]
    [InlineData("name = Neon Night", "name = ")]
    public void Parse_refuses_a_theme_with_an_invalid_identity(string original, string replacement)
    {
        ThemeFileParseResult result = ThemeFile.Parse(Neon.Replace(original, replacement, StringComparison.Ordinal));

        Assert.Null(result.Theme);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.IsError);
    }

    [Fact]
    public void Parse_defaults_platforms_to_the_base_theme_form_factor()
    {
        UserTheme theme = ThemeFile.Parse("[theme]\nid = droid\nname = Droid\nbase = android\n").Theme!;

        Assert.Equal([ThemePlatform.Mobile], theme.Platforms);
    }

    [Fact]
    public void Parse_refuses_a_file_over_64_KiB()
    {
        string huge = Neon + "\n# " + new string('x', ThemeFile.MaxFileBytes) + "\n";

        Assert.Null(ThemeFile.Parse(huge).Theme);
    }

    [Fact]
    public void Serialize_round_trips_through_Parse()
    {
        UserTheme theme = ThemeFile.Parse(Neon).Theme!;

        UserTheme reread = ThemeFile.Parse(ThemeFile.Serialize(theme)).Theme!;

        Assert.Equal(theme.Id, reread.Id);
        Assert.Equal(theme.Name, reread.Name);
        Assert.Equal(theme.Platforms, reread.Platforms);
        Assert.Equal(theme.Tokens, reread.Tokens);
    }

    [Theory]
    [InlineData(ThemeTokenType.Color, "#39ff14", true)]
    [InlineData(ThemeTokenType.Color, "rgb(57 255 20 / 50%)", true)]
    [InlineData(ThemeTokenType.Color, "rebeccapurple", true)]
    [InlineData(ThemeTokenType.Color, "var(--x)", false)]
    [InlineData(ThemeTokenType.Length, "0.5rem", true)]
    [InlineData(ThemeTokenType.Length, "calc(1px + 2px)", false)]
    [InlineData(ThemeTokenType.Duration, "150ms ease", true)]
    [InlineData(ThemeTokenType.Duration, "fast", false)]
    [InlineData(ThemeTokenType.FontFamily, "\"Fira Code\", monospace", true)]
    [InlineData(ThemeTokenType.FontFamily, "x; } body { color: red", false)]
    [InlineData(ThemeTokenType.Number, "600", true)]
    [InlineData(ThemeTokenType.Number, "", false)]
    public void Validator_accepts_only_allow_listed_values(ThemeTokenType type, string value, bool expected)
    {
        Assert.Equal(expected, ThemeTokenValidator.TryValidate(type, value, out _, out string? error));
        Assert.Equal(expected, error is null);
    }

    [Fact]
    public void Css_properties_add_rgb_companions_and_drop_animations_when_disabled()
    {
        UserTheme theme = ThemeFile.Parse(Neon).Theme!;

        IReadOnlyDictionary<string, string> animated = theme.ToCssProperties(animationsEnabled: true);
        IReadOnlyDictionary<string, string> still = theme.ToCssProperties(animationsEnabled: false);

        Assert.Equal("255, 0, 255", animated["--hos-accent-rgb"]);
        Assert.Equal("90ms ease-out", animated["--hos-transition-fast"]);
        Assert.False(still.ContainsKey("--hos-transition-fast"));
        Assert.Equal("#ff00ff", still["--hos-accent"]);
    }

    [Fact]
    public void Catalog_token_examples_are_all_valid()
    {
        Assert.All(ThemeTokenCatalog.All, token =>
            Assert.True(ThemeTokenValidator.TryValidate(token.Type, token.Example, out _, out string? error), $"{token.Name}: {error}"));
        Assert.Equal(ThemeTokenCatalog.All.Count, ThemeTokenCatalog.All.Select(token => token.Name).Distinct().Count());
    }

    [Fact]
    public void Library_refuses_built_in_ids_and_duplicates_and_prefers_etc()
    {
        UserThemeLibraryResult result = UserThemeLibrary.Build(
        [
            new ThemeFileSource("/etc/kazoos/themes/neon.theme", ThemeOrigin.System, Neon),
            new ThemeFileSource("/home/alice/.config/kazoos/themes/neon.theme", ThemeOrigin.User, Neon.Replace("Neon Night", "Mine", StringComparison.Ordinal)),
            new ThemeFileSource("/home/alice/.config/kazoos/themes/ubuntu.theme", ThemeOrigin.User, Neon.Replace("id = neon", "id = ubuntu", StringComparison.Ordinal)),
        ]);

        UserTheme theme = Assert.Single(result.Themes);
        Assert.Equal("Neon Night", theme.Name);
        Assert.Equal(ThemeOrigin.System, theme.Origin);
        Assert.Equal(2, result.Diagnostics.Count(diagnostic => diagnostic.IsError));
    }

    [Fact]
    public void Resolve_applies_user_tokens_and_falls_back_for_unknown_ids()
    {
        UserTheme neon = ThemeFile.Parse(Neon).Theme!;
        UserTheme[] themes = [neon];

        ResolvedTheme desktop = ResolvedTheme.Resolve("neon", ThemePlatform.Desktop, themes);
        ResolvedTheme mobile = ResolvedTheme.Resolve("neon", ThemePlatform.Mobile, themes);
        ResolvedTheme missing = ResolvedTheme.Resolve("deleted-theme", ThemePlatform.Desktop, themes);
        ResolvedTheme builtIn = ResolvedTheme.Resolve(WellKnownThemeIds.Ubuntu, ThemePlatform.Desktop, themes);

        Assert.Equal(new ResolvedTheme(WellKnownThemeIds.KazoOs, neon, false), desktop);
        // A desktop-based theme offered on mobile skins the mobile default theme.
        Assert.Equal(ThemePreferences.Default.MobileThemeId, mobile.BaseThemeId);
        Assert.Same(neon, mobile.UserTheme);
        Assert.Equal(new ResolvedTheme(ThemePreferences.Default.DesktopThemeId, null, true), missing);
        Assert.Equal(new ResolvedTheme(WellKnownThemeIds.Ubuntu, null, false), builtIn);
    }
}
