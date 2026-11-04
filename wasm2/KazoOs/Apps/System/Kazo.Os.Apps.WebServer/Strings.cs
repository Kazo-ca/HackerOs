using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.WebServer;

/// <summary>
/// Names the Web Server Manager's texts (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French. The window reads them with
/// <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>, <see cref="Manager.WebSiteStore"/> with
/// <see cref="Ambient"/>. What <c>hackwebd</c> writes (its logs, the pages and listings it serves) is
/// not translated: logs stay English for tools, served pages are the site's content.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }

    /// <summary>
    /// The texts in the language of the process running the current code
    /// (<see cref="Kazo.Os.AppSdk.HackExecutionContext"/>), English outside any app.
    /// </summary>
    internal static IStringLocalizer<Strings> Ambient { get; } = HackLocalization.GetLocalizer<Strings>();

    /// <summary>
    /// Picks <c>&lt;key&gt;.One</c> or <c>&lt;key&gt;.Other</c> for <paramref name="count"/> and formats it:
    /// English uses the singular for 1 only, French for 0 and 1 (CLDR plural rules of both languages).
    /// </summary>
    /// <param name="text">The localizer, whose culture decides the rule.</param>
    /// <param name="key">The key without its <c>.One</c>/<c>.Other</c> suffix.</param>
    /// <param name="count">The number counted, passed as <c>{0}</c>.</param>
    internal static string Count(IStringLocalizer<Strings> text, string key, int count)
    {
        bool french = text is HackStringLocalizer { Culture.TwoLetterISOLanguageName: "fr" };
        bool one = french ? count is 0 or 1 : count == 1;
        return text[key + (one ? ".One" : ".Other"), count];
    }
}
