using System.Collections;
using System.Globalization;
using System.Resources;

namespace Kazo.Os.AppSdk.VirtualKeyboard.Tests;

/// <summary>ADR 0104: the key caps of the on-screen keyboard exist in English and in French.</summary>
public sealed class VirtualKeyboardLocalizationTests
{
    private static readonly ResourceManager Resources =
        new("Kazo.Os.AppSdk.VirtualKeyboard.Resources.Strings", typeof(VirtualKeyboardOptions).Assembly);

    [Fact]
    public void Every_key_cap_exists_in_english_and_in_french()
    {
        HashSet<string> english = Keys(CultureInfo.InvariantCulture);
        HashSet<string> french = Keys(CultureInfo.GetCultureInfo("fr"));

        Assert.NotEmpty(english);
        Assert.Empty(english.Except(french));
        Assert.Empty(french.Except(english));
        Assert.Equal("Suppr", Resources.GetString("Key.Delete", CultureInfo.GetCultureInfo("fr")));
        Assert.Equal("Del", Resources.GetString("Key.Delete", CultureInfo.InvariantCulture));
    }

    private static HashSet<string> Keys(CultureInfo culture) =>
        Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false) is { } set
            ? [.. set.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)]
            : [];
}
