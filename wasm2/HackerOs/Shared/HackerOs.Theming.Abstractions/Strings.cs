using System.Globalization;
using System.Resources;

namespace HackerOs.Theming.Abstractions;

/// <summary>
/// Names the user-visible texts of the theme contracts (ADR 0104): token labels and descriptions,
/// and the diagnostics of theme files, in <c>Resources/Strings.resx</c> (English, neutral) and
/// <c>Resources/Strings.fr.resx</c> (French).
/// </summary>
/// <remarks>
/// This package stays free of any KazoOS or UI dependency, so the texts are read through a plain
/// <see cref="ResourceManager"/> with the culture the caller passes (an app's
/// <c>AppContext.Culture</c>, the platform's <c>SessionLocaleService.Culture</c>) instead of an
/// <c>IStringLocalizer</c>. A <see langword="null"/> culture reads the English texts.
/// </remarks>
internal sealed class Strings
{
    private static readonly ResourceManager Resources =
        new("HackerOs.Theming.Abstractions.Resources.Strings", typeof(Strings).Assembly);

    private Strings()
    {
    }

    /// <summary>Reads a text, or <see langword="null"/> when no resource has the key.</summary>
    /// <param name="key">Resource key.</param>
    /// <param name="culture">Language of the text; <see langword="null"/> reads English.</param>
    internal static string? Get(string key, CultureInfo? culture) =>
        Resources.GetString(key, culture ?? CultureInfo.InvariantCulture);

    /// <summary>Reads a text and formats it with <paramref name="arguments"/> in the same culture.</summary>
    /// <param name="culture">Language of the text; <see langword="null"/> reads English.</param>
    /// <param name="key">Resource key; used as the format when no resource has it.</param>
    /// <param name="arguments">Format arguments.</param>
    internal static string Format(CultureInfo? culture, string key, params object?[] arguments) =>
        string.Format(culture ?? CultureInfo.InvariantCulture, Get(key, culture) ?? key, arguments);
}

/// <summary>
/// A text that is translated only when it is shown: a resource key and its arguments, where an
/// argument may itself be a <see cref="ThemeText"/> (a validation reason inside a file warning).
/// Diagnostics keep one so the same warning reads in the language of whoever displays it.
/// </summary>
internal sealed class ThemeText
{
    /// <summary>Creates a deferred text.</summary>
    /// <param name="key">Resource key in <see cref="Strings"/>.</param>
    /// <param name="arguments">Format arguments.</param>
    public ThemeText(string key, params object?[] arguments)
    {
        Key = key;
        Arguments = arguments;
    }

    /// <summary>Gets the resource key.</summary>
    public string Key { get; }

    /// <summary>Gets the format arguments.</summary>
    public IReadOnlyList<object?> Arguments { get; }

    /// <summary>Formats the text and its nested texts in <paramref name="culture"/>.</summary>
    /// <param name="culture">Language; <see langword="null"/> formats English.</param>
    public string Format(CultureInfo? culture) =>
        Strings.Format(culture, Key, [.. Arguments.Select(argument => argument is ThemeText text ? text.Format(culture) : argument)]);
}
