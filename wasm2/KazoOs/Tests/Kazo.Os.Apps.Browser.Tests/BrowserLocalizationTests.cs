using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.Apps.Browser;
using Kazo.Os.Apps.Browser.Net;
using Xunit;

namespace Kazo.Os.Apps.Browser.Tests;

/// <summary>The Browser's own pages and chrome in French (ADR 0104); codes and names stay as they are.</summary>
public sealed class BrowserLocalizationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    [Fact]
    public void ErrorPages_InFrench_TranslateTextsButKeepCodes()
    {
        BrowserErrorView dns = BrowserErrorPages.ForNetworkError("ERR_NAME_NOT_RESOLVED", new Uri("https://intranet.hack/"), French);
        BrowserErrorView notFound = BrowserErrorPages.ForHttpStatus(404, new Uri("https://intranet.hack/missing"), French);
        BrowserErrorView crash = BrowserErrorPages.ForRendererCrash(null, killed: true, French);

        Assert.Equal("Ce site est inaccessible", dns.Title);
        Assert.Equal("Impossible de trouver l'adresse IP du serveur de intranet.hack.", dns.Message);
        Assert.Equal("ERR_NAME_NOT_RESOLVED", dns.Code);
        Assert.Equal("Cette page intranet.hack est introuvable", notFound.Title);
        Assert.Equal("HTTP ERROR 404", notFound.Code);
        Assert.Equal("Aïe, aïe, aïe !", crash.Title);
        Assert.Equal(BrowserErrorPages.RendererKilled, crash.Code);
    }

    [Fact]
    public void ErrorPages_WithoutCulture_StayInEnglish()
    {
        BrowserErrorView view = BrowserErrorPages.ForScriptNavigation();

        Assert.Equal("This page navigated by script", view.Title);
        Assert.Equal(["Reload the page.", "Use a link or a form for navigation."], view.Hints);
    }

    [Fact]
    public void DirectoryListing_InFrench_TranslatesHeadingsAndUnits()
    {
        string html = System.Net.WebUtility.HtmlDecode(DirectoryListingPage.Build("/home/neo/site", [
            new DirectoryListingEntry("notes.txt", false, 1500, new DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc)),
        ], French, CultureInfo.GetCultureInfo("fr-FR")));

        Assert.Contains("<title>Index de /home/neo/site/</title>", html, StringComparison.Ordinal);
        Assert.Contains("<th>Nom</th><th>Taille</th><th>Date de modification</th>", html, StringComparison.Ordinal);
        Assert.Contains("[répertoire parent]", html, StringComparison.Ordinal);
        Assert.Contains("1,5 ko", html, StringComparison.Ordinal);
        Assert.Contains("notes.txt", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Resources_EnglishAndFrench_HaveTheSameKeys()
    {
        ResourceManager resources = new("Kazo.Os.Apps.Browser.Resources.Strings", typeof(BrowserErrorPages).Assembly);

        Assert.Equal(Keys(resources, CultureInfo.InvariantCulture), Keys(resources, French));
    }

    private static SortedSet<string> Keys(ResourceManager resources, CultureInfo culture)
    {
        ResourceSet set = resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No {culture.Name} resources.");
        return new SortedSet<string>(set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key), StringComparer.Ordinal);
    }
}
