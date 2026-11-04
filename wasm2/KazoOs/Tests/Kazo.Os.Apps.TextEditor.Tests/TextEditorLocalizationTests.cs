using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Kazo.Os.Apps.TextEditor.Tests;

/// <summary>The Text Editor speaks the language of its process (ADR 0104).</summary>
public sealed class TextEditorLocalizationTests
{
    private static IStringLocalizer<Strings> French => new LocaleTestContext("fr_FR.UTF-8").GetLocalizer<Strings>();

    [Fact]
    public void Document_TitlesFollowTheWindowLanguage()
    {
        TextEditorDocument document = new(French);
        Assert.Equal("Sans titre", document.Title);

        document.RestoreDraft("text", "/home/alice/notes.txt");
        Assert.Equal("• notes.txt (récupéré)", document.GetWindowTitle());

        document.RestoreDraft("text", null);
        Assert.Equal("Sans titre (récupéré)", document.Title);
    }

    [Theory]
    [InlineData(0, "0 remplacement")]
    [InlineData(1, "1 remplacement")]
    [InlineData(12, "12 remplacements")]
    public void ReplacementCount_IsWrittenInFrench(int count, string expected) =>
        Assert.Equal(expected, TextEditorFindEngine.DescribeReplacementCount(count, French));

    [Fact]
    public void WindowTexts_FollowTheProcessLanguage()
    {
        IStringLocalizer<Strings> english = new LocaleTestContext("en_US.UTF-8").GetLocalizer<Strings>();

        Assert.Equal("Enregistrer sous…", French["Menu.SaveAs"]);
        Assert.Equal("Occurrence 2/5", French["Find.MatchPosition", 2, 5]);
        Assert.Equal("Save As...", english["Menu.SaveAs"]);
        Assert.Equal("1 match", Strings.Plural(english, "Find.Matches", 1));
    }
}
