using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.AppSdk.Globalization;

namespace Kazo.Os.AppSdk.Web.Blazor.Tests;

/// <summary>ADR 0104: the site router's default texts exist in English and in French.</summary>
public sealed class WebBlazorLocalizationTests
{
    [Fact]
    public void Every_site_sdk_text_exists_in_english_and_in_french()
    {
        ResourceManager resources = KazoLocalization.GetResourceManager(typeof(Strings));
        CultureInfo french = CultureInfo.GetCultureInfo("fr");

        HashSet<string> englishKeys = Keys(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));
        HashSet<string> frenchKeys = Keys(resources.GetResourceSet(french, createIfNotExists: true, tryParents: false));

        Assert.NotEmpty(englishKeys);
        Assert.Empty(englishKeys.Except(frenchKeys));
        Assert.Empty(frenchKeys.Except(englishKeys));
        Assert.Equal("Désolé, il n'y a rien à cette adresse.", resources.GetString("Router.NotFound", french));

        static HashSet<string> Keys(ResourceSet? set) =>
            set is null ? [] : [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];
    }
}
