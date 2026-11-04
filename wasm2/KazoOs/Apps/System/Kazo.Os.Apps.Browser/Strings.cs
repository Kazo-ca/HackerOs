using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Browser;

/// <summary>
/// Names the Browser's own texts (ADR 0104): its chrome, the pages it generates itself (error pages,
/// directory listings) and its developer tools, in <c>Resources/Strings.resx</c> (English) and
/// <c>Resources/Strings.fr.resx</c> (French). Windows read them with
/// <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>; the static page builders take a culture through
/// <see cref="For(CultureInfo?)"/>. The content of web sites and the console messages of the
/// developer tools (web-platform messages, as Chromium prints them) are not translated.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }

    /// <summary>Returns the Browser's texts in <paramref name="culture"/>, or in English when it is <see langword="null"/>.</summary>
    /// <param name="culture">The language of the process the text is shown to.</param>
    internal static IStringLocalizer<Strings> For(CultureInfo? culture) =>
        culture is null
            ? new KazoStringLocalizer<Strings>(static () => KazoLocale.DefaultLanguage)
            : new KazoStringLocalizer<Strings>(() => culture);
}
