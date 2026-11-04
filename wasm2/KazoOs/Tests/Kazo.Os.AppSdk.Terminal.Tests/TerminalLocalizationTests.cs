using System.Collections;
using System.Globalization;
using System.Resources;
using Kazo.Os.AppSdk.Globalization;
using Xunit;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>ADR 0104: the terminal panel's own texts exist in English and in French.</summary>
public sealed class TerminalLocalizationTests
{
    [Fact]
    public void Every_terminal_text_exists_in_english_and_in_french()
    {
        ResourceManager resources = HackLocalization.GetResourceManager(typeof(Strings));
        CultureInfo french = CultureInfo.GetCultureInfo("fr");

        HashSet<string> englishKeys = Keys(resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));
        HashSet<string> frenchKeys = Keys(resources.GetResourceSet(french, createIfNotExists: true, tryParents: false));

        Assert.NotEmpty(englishKeys);
        Assert.Empty(englishKeys.Except(frenchKeys));
        Assert.Empty(frenchKeys.Except(englishKeys));
        Assert.Equal("[hsh s'est terminé avec le code {0}]", resources.GetString("Panel.ShellExited", french));
        Assert.Equal("[hsh exited with status {0}]", resources.GetString("Panel.ShellExited", CultureInfo.InvariantCulture));

        static HashSet<string> Keys(ResourceSet? set) =>
            set is null ? [] : [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)];
    }
}
