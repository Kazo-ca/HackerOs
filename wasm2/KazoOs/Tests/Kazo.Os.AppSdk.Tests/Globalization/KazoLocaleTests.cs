using System.Globalization;
using Kazo.Os.AppSdk.Globalization;

namespace Kazo.Os.AppSdk.Tests.Globalization;

/// <summary>Locale rules of ADR 0104: glibc lookup, POSIX names, <c>locale.conf</c>.</summary>
public sealed class KazoLocaleTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        Dictionary<string, string> map = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.GetValueOrDefault(name);
    }

    [Theory]
    [InlineData("fr_CA.UTF-8", "fr")]
    [InlineData("fr_FR", "fr")]
    [InlineData("fr", "fr")]
    [InlineData("en_GB.UTF-8", "en")]
    [InlineData("de_DE.UTF-8", "en")]
    [InlineData("C", "en")]
    [InlineData("garbage!!", "en")]
    [InlineData("", "en")]
    public void Any_French_locale_gives_French_and_everything_else_English(string locale, string expected) =>
        Assert.Equal(expected, KazoLocale.ResolveLanguage(Env(("LANG", locale))).Name);

    [Fact]
    public void LC_ALL_beats_LC_MESSAGES_which_beats_LANG_and_empty_values_count_as_unset()
    {
        Assert.Equal("fr", KazoLocale.ResolveLanguage(Env(("LANG", "en_US.UTF-8"), ("LC_MESSAGES", "fr_FR.UTF-8"))).Name);
        Assert.Equal("en", KazoLocale.ResolveLanguage(Env(("LANG", "fr_FR.UTF-8"), ("LC_MESSAGES", "fr_FR.UTF-8"), ("LC_ALL", "C"))).Name);
        Assert.Equal("fr", KazoLocale.ResolveLanguage(Env(("LANG", "fr_FR.UTF-8"), ("LC_ALL", ""))).Name);
        Assert.Equal("en", KazoLocale.ResolveLanguage(Env()).Name);
    }

    [Fact]
    public void Formats_use_the_full_culture_of_their_category()
    {
        Assert.Equal("fr-CA", KazoLocale.ResolveFormatCulture(Env(("LANG", "fr_CA.UTF-8"))).Name);
        Assert.Equal("en-GB", KazoLocale.ResolveFormatCulture(Env(("LANG", "fr_CA.UTF-8"), ("LC_TIME", "en_GB.UTF-8"))).Name);
        Assert.Equal("fr-CA", KazoLocale.ResolveFormatCulture(Env(("LANG", "fr_CA.UTF-8"), ("LC_TIME", "en_GB.UTF-8")), KazoLocale.NumericVariable).Name);
        Assert.Same(CultureInfo.InvariantCulture, KazoLocale.ResolveFormatCulture(Env(("LANG", "C.UTF-8"))));
        Assert.Equal("en-US", KazoLocale.ResolveFormatCulture(Env(("LANG", "zz_QQ.UTF-8"))).Name);
    }

    [Theory]
    [InlineData("fr_CA.UTF-8", "fr-CA")]
    [InlineData("fr_FR.utf8@euro", "fr-FR")]
    [InlineData("POSIX", "")]
    [InlineData("zz_QQ", null)]
    [InlineData("not a locale", null)]
    [InlineData(null, null)]
    public void POSIX_names_map_to_cultures(string? locale, string? expected) =>
        Assert.Equal(expected, KazoLocale.ToCulture(locale)?.Name);

    [Fact]
    public void Locale_names_round_trip_and_availability_ignores_the_charset()
    {
        Assert.Equal("fr_CA.UTF-8", KazoLocale.ToLocaleName(CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("C.UTF-8", KazoLocale.ToLocaleName(CultureInfo.InvariantCulture));
        Assert.True(KazoLocale.IsAvailable("fr_CA.utf8"));
        Assert.True(KazoLocale.IsAvailable("en_US"));
        Assert.False(KazoLocale.IsAvailable("de_DE.UTF-8"));
        Assert.Equal(8, KazoLocale.AvailableLocales.Count);
    }

    [Fact]
    public void Locale_conf_is_parsed_like_systemd_and_bad_lines_are_reported()
    {
        List<(int Line, string Message)> warnings = [];

        IReadOnlyDictionary<string, string> variables = KazoLocale.ParseLocaleConf(
            "# system locale\r\nLANG=\"fr_CA.UTF-8\"\n\nLC_TIME='en_GB.UTF-8'\nEDITOR=nano\nnot a pair\nLANG=fr_FR.UTF-8\n",
            warnings);

        Assert.Equal("fr_FR.UTF-8", variables["LANG"]);
        Assert.Equal("en_GB.UTF-8", variables["LC_TIME"]);
        Assert.False(variables.ContainsKey("EDITOR"));
        Assert.Equal([5, 6], warnings.Select(w => w.Line));
        Assert.Empty(KazoLocale.ParseLocaleConf(null));
    }

    [Fact]
    public void Locale_conf_is_written_with_LANG_first_and_reads_back()
    {
        string content = KazoLocale.FormatLocaleConf(
            new Dictionary<string, string> { ["LC_TIME"] = "en_GB.UTF-8", ["LANG"] = "fr_CA.UTF-8" },
            "written by Settings");

        Assert.Equal("# written by Settings\nLANG=fr_CA.UTF-8\nLC_TIME=en_GB.UTF-8\n", content);
        Assert.Equal("fr_CA.UTF-8", KazoLocale.ParseLocaleConf(content)["LANG"]);
        Assert.Throws<ArgumentException>(() => KazoLocale.FormatLocaleConf(new Dictionary<string, string> { ["EDITOR"] = "nano" }));
        Assert.Throws<ArgumentException>(() => KazoLocale.FormatLocaleConf(new Dictionary<string, string> { ["LANG"] = "fr\nPATH=/x" }));
    }
}
