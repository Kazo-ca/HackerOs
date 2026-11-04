using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>
/// Names the Code Editor's texts (ADR 0104): <c>Resources/Strings.resx</c> in English and
/// <c>Resources/Strings.fr.resx</c> in French. Components read them with
/// <c>AppContext.GetLocalizer&lt;Strings&gt;()</c>; the workbench's services (file, recovery and
/// workspace operations) with <see cref="Ambient"/>, the language of the process whose code is running.
/// Monaco's own widgets (find box, command palette) keep their built-in English texts.
/// </summary>
internal sealed class Strings
{
    private Strings()
    {
    }

    /// <summary>
    /// The Code Editor's texts in the language of the process running the current code
    /// (<see cref="Kazo.Os.AppSdk.HackExecutionContext"/>), English outside any app (a unit test).
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
