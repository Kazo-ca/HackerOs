using System.Globalization;
using HackerOs.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.Store;

/// <summary>
/// Names the Hack Store's texts (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French, read with <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }

    /// <summary>
    /// Reads <c>&lt;key&gt;.One</c> or <c>&lt;key&gt;.Many</c> formatted with <paramref name="count"/>,
    /// following the plural rule of the localizer's language: French treats 0 and 1 as singular,
    /// English only 1.
    /// </summary>
    /// <param name="text">The localizer to read from.</param>
    /// <param name="key">The key without its <c>.One</c>/<c>.Many</c> suffix.</param>
    /// <param name="count">The number counted.</param>
    internal static string Plural(IStringLocalizer text, string key, int count)
    {
        CultureInfo culture = text is HackStringLocalizer hack ? hack.Culture : HackLocale.DefaultLanguage;
        bool one = culture.TwoLetterISOLanguageName == "fr" ? count is 0 or 1 : count == 1;
        return text[one ? key + ".One" : key + ".Many", count];
    }
}
