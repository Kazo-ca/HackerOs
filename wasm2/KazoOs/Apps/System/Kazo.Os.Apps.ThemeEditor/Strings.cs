using System.Globalization;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Theming.Abstractions;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.ThemeEditor;

/// <summary>
/// Names the Theme Editor's texts (ADR 0104): <c>Resources/Strings.resx</c> in English and
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
        CultureInfo culture = text is KazoStringLocalizer kazo ? kazo.Culture : KazoLocale.DefaultLanguage;
        bool one = culture.TwoLetterISOLanguageName == "fr" ? count is 0 or 1 : count == 1;
        return text[one ? key + ".One" : key + ".Many", count];
    }

    /// <summary>
    /// Splits a one-argument text around its <c>{0}</c>, so markup can wrap the argument (a bold
    /// name, a styled path) while the sentence around it stays translated and keeps scoped CSS.
    /// </summary>
    /// <param name="text">The localized format, such as <c>A file named {0} already exists here.</c></param>
    internal static (string Before, string After) Around(string text)
    {
        int index = text.IndexOf("{0}", StringComparison.Ordinal);
        return index < 0 ? (text, string.Empty) : (text[..index], text[(index + 3)..]);
    }

    /// <summary>
    /// The language a localizer reads, for the texts of <c>Kazo.Os.Theming.Abstractions</c> (token
    /// labels and descriptions, validation reasons, theme file warnings), which take a culture
    /// instead of a localizer; <see langword="null"/> (English) for any other localizer.
    /// </summary>
    /// <param name="text">The localizer of the window or component.</param>
    internal static CultureInfo? CultureOf(IStringLocalizer text) => text is KazoStringLocalizer kazo ? kazo.Culture : null;

    /// <summary>The name of a token group (tab title, documentation heading); an unknown group keeps its enum name.</summary>
    /// <param name="text">The localizer to read from.</param>
    /// <param name="group">The token group.</param>
    internal static string GroupLabel(IStringLocalizer text, ThemeTokenGroup group)
    {
        LocalizedString label = text[$"Group.{group}.Label"];
        return label.ResourceNotFound ? group.ToString() : label.Value;
    }

    /// <summary>The name of a token value type; an unknown type keeps its enum name.</summary>
    /// <param name="text">The localizer to read from.</param>
    /// <param name="type">The value type.</param>
    internal static string TypeLabel(IStringLocalizer text, ThemeTokenType type)
    {
        LocalizedString label = text[$"Type.{type}"];
        return label.ResourceNotFound ? type.ToString() : label.Value;
    }
}
