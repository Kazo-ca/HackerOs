using System.Globalization;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;
using Xunit;

namespace HackerOs.Apps.FileExplorer.Tests;

/// <summary>The File Explorer speaks the language of its process and formats with its locale (ADR 0104).</summary>
public sealed class FileExplorerLocalizationTests
{
    private static readonly IAppExecutionContext FrenchContext = new LocaleTestContext("fr_FR.UTF-8");
    private static readonly IAppExecutionContext EnglishContext = new LocaleTestContext("en_US.UTF-8");

    [Fact]
    public void ToolbarAndStatus_FollowTheProcessLanguage()
    {
        IStringLocalizer<Strings> french = FrenchContext.GetLocalizer<Strings>();
        IStringLocalizer<Strings> english = EnglishContext.GetLocalizer<Strings>();

        Assert.Equal("Nouveau dossier", french["Toolbar.NewFolder"]);
        Assert.Equal("Aucun résultat pour « zz ».", french["Search.NoResults", "zz"]);
        Assert.Equal("1 élément", Strings.Plural(french, "Status.Items", 1));
        Assert.Equal("3 éléments", Strings.Plural(french, "Status.Items", 3));
        Assert.Equal("New Folder", english["Toolbar.NewFolder"]);
        Assert.Equal("3 item(s)", Strings.Plural(english, "Status.Items", 3));
    }

    [Fact]
    public void Around_SplitsASentenceAroundItsArgument()
    {
        IStringLocalizer<Strings> french = FrenchContext.GetLocalizer<Strings>();

        Assert.Equal(("Un dossier nommé ", " existe déjà ici."), Strings.Around(french["Conflict.FolderExists"]));
        Assert.Equal(("Moving ", string.Empty), Strings.Around(EnglishContext.GetLocalizer<Strings>()["Conflict.Moving"]));
    }

    [Fact]
    public void TrashSizesAndDates_UseTheFormatCulture()
    {
        IStringLocalizer<Strings> french = FrenchContext.GetLocalizer<Strings>();
        DateTimeOffset deletedAt = new(2026, 10, 17, 13, 37, 0, TimeSpan.Zero);

        Assert.Equal("1,5 Ko", TrashView.FormatBytes(1536, FrenchContext.FormatCulture, french));
        Assert.Equal("1.5 KB", TrashView.FormatBytes(1536, EnglishContext.FormatCulture, EnglishContext.GetLocalizer<Strings>()));
        Assert.Equal("17/10/2026 13:37", TrashView.FormatDate(deletedAt, FrenchContext.FormatCulture));
        Assert.Equal("—", TrashView.FormatDate(null, CultureInfo.InvariantCulture));
    }
}
