using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Theming.Abstractions;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Kazo.Os.Apps.ThemeEditor.Tests;

/// <summary>The Theme Editor speaks the language of its process (ADR 0104).</summary>
public sealed class ThemeEditorLocalizationTests
{
    private static IStringLocalizer<Strings> French => new LocaleTestContext("fr_FR.UTF-8").GetLocalizer<Strings>();

    [Fact]
    public void Document_NamesAndProblems_FollowTheWindowLanguage()
    {
        ThemeEditorDocument document = new(French);
        Assert.StartsWith("Mon ", document.Name, StringComparison.Ordinal);

        document.SetId(WellKnownThemeIds.Ubuntu);
        document.ForDesktop = false;

        Assert.Equal(
            [
                $"« {WellKnownThemeIds.Ubuntu} » est l'identifiant d'un thème intégré ; choisissez-en un autre.",
                "Proposez le thème sur le bureau, sur mobile, ou les deux."
            ],
            document.GetProblems());
    }

    [Fact]
    public void GroupAndTypeNames_AreTranslated()
    {
        IStringLocalizer<Strings> english = new LocaleTestContext("en_US.UTF-8").GetLocalizer<Strings>();

        Assert.Equal("Barre des tâches", Strings.GroupLabel(French, ThemeTokenGroup.Taskbar));
        Assert.Equal("Couleur", Strings.TypeLabel(French, ThemeTokenType.Color));
        Assert.Equal("Taskbar", Strings.GroupLabel(english, ThemeTokenGroup.Taskbar));
        Assert.Equal("FontFamily", Strings.TypeLabel(english, ThemeTokenType.FontFamily));
    }

    [Fact]
    public void EveryTokenGroupAndValueType_HasAText()
    {
        foreach (ThemeTokenGroup group in Enum.GetValues<ThemeTokenGroup>())
        {
            Assert.False(French[$"Group.{group}.Label"].ResourceNotFound, group.ToString());
            Assert.False(French[$"Group.{group}.Intro"].ResourceNotFound, group.ToString());
        }

        foreach (ThemeTokenType type in Enum.GetValues<ThemeTokenType>())
        {
            Assert.False(French[$"Type.{type}"].ResourceNotFound, type.ToString());
            Assert.False(French[$"Docs.Types.{type}"].ResourceNotFound, type.ToString());
        }
    }
}
