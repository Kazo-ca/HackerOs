using System.Globalization;

namespace Kazo.Os.Theming.Abstractions;

/// <summary>
/// Value grammar of one theme token (ADR 0103 §2.3). Every value read from a user theme file is
/// checked against its token's type before it may reach the page, so a theme stays "data only".
/// </summary>
public enum ThemeTokenType
{
    /// <summary>A CSS color: <c>#rgb</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c>/<c>hsl()</c> or a named color.</summary>
    Color,

    /// <summary>A CSS length: <c>0</c> or a number with <c>px</c>, <c>rem</c>, <c>em</c>, <c>%</c>, <c>vh</c> or <c>vw</c>.</summary>
    Length,

    /// <summary>A duration (<c>120ms</c>, <c>0.2s</c>), optionally followed by an easing keyword.</summary>
    Duration,

    /// <summary>A comma-separated font family list of quoted names or plain identifiers.</summary>
    FontFamily,

    /// <summary>A plain decimal number (font weight, opacity).</summary>
    Number
}

/// <summary>Editor groups of the theme tokens, in the order the Theme Editor shows them.</summary>
public enum ThemeTokenGroup
{
    /// <summary>Surfaces, text, borders, accent, status and desktop colors.</summary>
    Colors,

    /// <summary>Font families, sizes and weights.</summary>
    Typography,

    /// <summary>Corner radii, control heights and gaps.</summary>
    Spacing,

    /// <summary>Window frame, title bar and window buttons.</summary>
    Windows,

    /// <summary>Taskbar, its buttons and the launcher.</summary>
    Taskbar,

    /// <summary>Transition durations.</summary>
    Animations
}

/// <summary>Describes one CSS custom property a user theme may set (ADR 0103 §2.3).</summary>
/// <param name="Name">CSS custom property name, such as <c>--hos-accent</c>.</param>
/// <param name="Type">Value grammar enforced when a theme file is read.</param>
/// <param name="Group">Editor group.</param>
/// <param name="Label">Short human-readable label, in English; <see cref="GetLabel"/> translates it.</param>
/// <param name="Description">What the token changes on screen, in English (used by the editor's help and documentation); <see cref="GetDescription"/> translates it.</param>
/// <param name="Example">A valid sample value.</param>
/// <param name="RgbCompanion">
/// Name of a companion token holding the same color as an <c>r, g, b</c> triplet (for
/// <c>rgb(var(--x-rgb) / 20%)</c> mixes), derived automatically when this color is set; otherwise
/// <see langword="null"/>.
/// </param>
public sealed record ThemeTokenDefinition(
    string Name,
    ThemeTokenType Type,
    ThemeTokenGroup Group,
    string Label,
    string Description,
    string Example,
    string? RgbCompanion = null)
{
    /// <summary>
    /// Gets the label in a language (ADR 0104). <see cref="Label"/> stays the English text; the
    /// translation is looked up by token name, so a token unknown to the resources keeps its
    /// English label.
    /// </summary>
    /// <param name="culture">
    /// Language of the reader: an app passes <c>AppContext.Culture</c>, the platform
    /// <c>SessionLocaleService.Culture</c>; <see langword="null"/> returns <see cref="Label"/>.
    /// </param>
    public string GetLabel(CultureInfo? culture) =>
        culture is null ? Label : Strings.Get($"{ResourceKey}.Label", culture) ?? Label;

    /// <summary>Gets the description in a language (ADR 0104); see <see cref="GetLabel"/>.</summary>
    /// <param name="culture">Language of the reader; <see langword="null"/> returns <see cref="Description"/>.</param>
    public string GetDescription(CultureInfo? culture) =>
        culture is null ? Description : Strings.Get($"{ResourceKey}.Description", culture) ?? Description;

    /// <summary>
    /// Gets the resource key prefix of the token: <c>--hos-bg-base</c> becomes <c>Token.BgBase</c>.
    /// The CSS name itself is never translated.
    /// </summary>
    internal string ResourceKey => GetResourceKey(Name);

    /// <summary>Converts a token name to its resource key prefix.</summary>
    /// <param name="name">CSS custom property name.</param>
    internal static string GetResourceKey(string name)
    {
        string bare = name.StartsWith("--hos-", StringComparison.Ordinal) ? name[6..] : name.TrimStart('-');
        return "Token." + string.Concat(bare
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
