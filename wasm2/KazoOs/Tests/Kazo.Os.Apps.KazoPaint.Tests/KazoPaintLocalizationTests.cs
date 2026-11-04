using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Kazo.Os.Apps.KazoPaint.Tests;

/// <summary>Hack Paint speaks the language of its process (ADR 0104).</summary>
public sealed class KazoPaintLocalizationTests
{
    [Fact]
    public void WindowTexts_FollowTheProcessLanguage()
    {
        IStringLocalizer<Strings> french = new LocaleTestContext("fr_FR.UTF-8").GetLocalizer<Strings>();
        IStringLocalizer<Strings> english = new LocaleTestContext("en_US.UTF-8").GetLocalizer<Strings>();

        Assert.Equal("Enregistrer sous…", french["Button.SaveAs"]);
        Assert.Equal("Save As…", english["Button.SaveAs"]);
        Assert.Equal("Sans titre — Hack Paint", PaintTitle.Format(null, dirty: false, french["Title.Untitled"]));
    }

    [Fact]
    public void FileChecks_ReportInTheLanguageOfTheRunningProcess()
    {
        using (KazoExecutionContext.Enter(new LocaleTestContext("fr_FR.UTF-8")))
        {
            Assert.Equal(
                "Ce n'est pas une image PNG, JPEG, BMP, GIF ou WebP, ou le fichier est endommagé.",
                PaintFileStore.Inspect([1, 2, 3]).Error);
        }

        Assert.Equal(
            "This is not a PNG, JPEG, BMP, GIF or WebP image, or the file is damaged.",
            PaintFileStore.Inspect([1, 2, 3]).Error);
    }
}
