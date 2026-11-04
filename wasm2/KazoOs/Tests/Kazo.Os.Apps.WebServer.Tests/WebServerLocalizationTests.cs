using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.AppSdk.Globalization;

namespace Kazo.Os.Apps.WebServer.Tests;

/// <summary>The Web Server Manager's texts in English and French (ADR 0104).</summary>
public sealed class WebServerLocalizationTests
{
    private static readonly ResourceManager Resources =
        new("Kazo.Os.Apps.WebServer.Resources.Strings", typeof(WebServerManagerApp).Assembly);

    [Fact]
    public void Texts_InFrench_AreTranslated_AndKeepCommandNames()
    {
        HackStringLocalizer french = new(Resources, static () => CultureInfo.GetCultureInfo("fr"));
        HackStringLocalizer english = new(Resources, static () => HackLocale.DefaultLanguage);

        Assert.Equal("hackwebd en cours d'exécution · pid 42", french["Daemon.Running", 42].Value);
        Assert.Equal("hackwebd running · pid 42", english["Daemon.Running", 42].Value);
        Assert.Equal("+ Nouveau site", french["Toolbar.NewSite"].Value);
        Assert.Equal("Supprimer /etc/hackweb/sites/docs.site ? hackwebd cesse immédiatement de servir « docs ». Les fichiers du site sont conservés.",
            french["Dialog.Delete.Message", "/etc/hackweb/sites/docs.site", "docs"].Value);
    }

    [Fact]
    public void Resources_EnglishAndFrench_HaveTheSameKeys()
    {
        Assert.Equal(Keys(CultureInfo.InvariantCulture), Keys(CultureInfo.GetCultureInfo("fr")));
    }

    private static SortedSet<string> Keys(CultureInfo culture)
    {
        ResourceSet set = Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No {culture.Name} resources.");
        return new SortedSet<string>(set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key), StringComparer.Ordinal);
    }
}
