using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;
using Xunit;

namespace HackerOs.Apps.IconViewer.Tests;

/// <summary>The Icon Viewer speaks the language of its process (ADR 0104).</summary>
public sealed class IconViewerLocalizationTests
{
    [Fact]
    public void Texts_FollowTheProcessLanguage()
    {
        IStringLocalizer<Strings> french = new LocaleTestContext("fr_FR.UTF-8").GetLocalizer<Strings>();
        IStringLocalizer<Strings> english = new LocaleTestContext("en_US.UTF-8").GetLocalizer<Strings>();

        Assert.Equal("Bibliothèque d'icônes KazoOS", french["Header.Title"]);
        Assert.Equal("Aucune icône ne correspond à « zz ».", french["Empty", "zz"]);
        Assert.Equal("KazoOS Icon Library", english["Header.Title"]);
    }

    [Theory]
    [InlineData("fr_FR.UTF-8", 0, "0 icône")]
    [InlineData("fr_FR.UTF-8", 1, "1 icône")]
    [InlineData("fr_FR.UTF-8", 2, "2 icônes")]
    [InlineData("en_US.UTF-8", 0, "0 icons")]
    [InlineData("en_US.UTF-8", 1, "1 icon")]
    public void Count_UsesThePluralRuleOfTheLanguage(string locale, int count, string expected)
    {
        IStringLocalizer<Strings> text = new LocaleTestContext(locale).GetLocalizer<Strings>();

        Assert.Equal(expected, Strings.Plural(text, "Count", count));
    }
}
