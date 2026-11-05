using System.Globalization;
using System.Text.RegularExpressions;

namespace Kazo.Os.Theming.Abstractions;

/// <summary>
/// Checks theme token values against their <see cref="ThemeTokenType"/> (ADR 0103 §2.3, §2.4).
/// The grammars are deliberately narrow allow-lists: nothing that could close a declaration, open a
/// block, load a resource (<c>url()</c>, <c>@import</c>, <c>image-set()</c>) or reference another
/// property passes, so a theme file can only ever be data.
/// </summary>
public static partial class ThemeTokenValidator
{
    /// <summary>Longest accepted value, in characters.</summary>
    public const int MaxValueLength = 200;

    private static readonly HashSet<string> NamedColors = new(
        """
        aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue blueviolet brown
        burlywood cadetblue chartreuse chocolate coral cornflowerblue cornsilk crimson cyan darkblue darkcyan
        darkgoldenrod darkgray darkgreen darkgrey darkkhaki darkmagenta darkolivegreen darkorange darkorchid darkred
        darksalmon darkseagreen darkslateblue darkslategray darkslategrey darkturquoise darkviolet deeppink deepskyblue
        dimgray dimgrey dodgerblue firebrick floralwhite forestgreen fuchsia gainsboro ghostwhite gold goldenrod gray
        green greenyellow grey honeydew hotpink indianred indigo ivory khaki lavender lavenderblush lawngreen
        lemonchiffon lightblue lightcoral lightcyan lightgoldenrodyellow lightgray lightgreen lightgrey lightpink
        lightsalmon lightseagreen lightskyblue lightslategray lightslategrey lightsteelblue lightyellow lime limegreen
        linen magenta maroon mediumaquamarine mediumblue mediumorchid mediumpurple mediumseagreen mediumslateblue
        mediumspringgreen mediumturquoise mediumvioletred midnightblue mintcream mistyrose moccasin navajowhite navy
        oldlace olive olivedrab orange orangered orchid palegoldenrod palegreen paleturquoise palevioletred papayawhip
        peachpuff peru pink plum powderblue purple rebeccapurple red rosybrown royalblue saddlebrown salmon sandybrown
        seagreen seashell sienna silver skyblue slateblue slategray slategrey snow springgreen steelblue tan teal
        thistle tomato turquoise violet wheat white whitesmoke yellow yellowgreen transparent
        """.Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Validates and normalizes (trims) one value for a token type.</summary>
    /// <param name="type">Token type.</param>
    /// <param name="value">Raw value from a theme file or the editor.</param>
    /// <param name="normalized">The trimmed value when valid.</param>
    /// <param name="error">A short reason in English when invalid, for the warning or the editor.</param>
    /// <returns><see langword="true"/> when the value is acceptable for <paramref name="type"/>.</returns>
    public static bool TryValidate(ThemeTokenType type, string? value, out string normalized, out string? error) =>
        TryValidate(type, value, culture: null, out normalized, out error);

    /// <summary>Validates and normalizes (trims) one value for a token type, explaining a refusal in a language.</summary>
    /// <param name="type">Token type.</param>
    /// <param name="value">Raw value from a theme file or the editor.</param>
    /// <param name="culture">
    /// Language of <paramref name="error"/> (ADR 0104): an app passes <c>AppContext.Culture</c>;
    /// <see langword="null"/> reads English.
    /// </param>
    /// <param name="normalized">The trimmed value when valid.</param>
    /// <param name="error">A short reason when invalid, for the warning or the editor.</param>
    /// <returns><see langword="true"/> when the value is acceptable for <paramref name="type"/>.</returns>
    public static bool TryValidate(ThemeTokenType type, string? value, CultureInfo? culture, out string normalized, out string? error)
    {
        bool valid = Check(type, value, out normalized, out ThemeText? reason);
        error = reason?.Format(culture);
        return valid;
    }

    /// <summary>Validates one value for a named token of <see cref="ThemeTokenCatalog"/>.</summary>
    /// <param name="tokenName">Token name.</param>
    /// <param name="value">Raw value.</param>
    /// <param name="normalized">The trimmed value when valid.</param>
    /// <param name="error">Why the token or value was refused, in English.</param>
    /// <returns><see langword="true"/> when the token is known and the value valid.</returns>
    public static bool TryValidate(string tokenName, string? value, out string normalized, out string? error)
    {
        bool valid = Check(tokenName, value, out normalized, out ThemeText? reason);
        error = reason?.Format(null);
        return valid;
    }

    /// <summary>Validates one value for a named token, keeping the reason translatable (for file diagnostics).</summary>
    internal static bool Check(string tokenName, string? value, out string normalized, out ThemeText? error)
    {
        if (!ThemeTokenCatalog.TryGet(tokenName, out ThemeTokenDefinition? token))
        {
            normalized = string.Empty;
            error = new ThemeText("Validator.UnknownToken", tokenName);
            return false;
        }

        return Check(token.Type, value, out normalized, out error);
    }

    // The checks themselves; the reason stays a resource key until someone shows it.
    private static bool Check(ThemeTokenType type, string? value, out string normalized, out ThemeText? error)
    {
        normalized = (value ?? string.Empty).Trim();
        error = null;
        if (normalized.Length == 0)
        {
            error = new ThemeText("Validator.Empty");
            return false;
        }

        if (normalized.Length > MaxValueLength)
        {
            error = new ThemeText("Validator.TooLong", MaxValueLength);
            return false;
        }

        bool valid = type switch
        {
            ThemeTokenType.Color => IsColor(normalized),
            ThemeTokenType.Length => LengthPattern().IsMatch(normalized),
            ThemeTokenType.Duration => DurationPattern().IsMatch(normalized),
            ThemeTokenType.FontFamily => FontFamilyPattern().IsMatch(normalized),
            ThemeTokenType.Number => NumberPattern().IsMatch(normalized),
            _ => false
        };

        if (!valid)
        {
            error = new ThemeText(Enum.IsDefined(type) ? $"Validator.Expected.{type}" : "Validator.UnknownType");
        }

        return valid;
    }

    /// <summary>
    /// Converts a validated color to the <c>r, g, b</c> triplet used by the <c>-rgb</c> companion
    /// tokens. Only hexadecimal and plain <c>rgb()</c> colors can be converted.
    /// </summary>
    /// <param name="color">A color accepted by <see cref="TryValidate(ThemeTokenType, string?, out string, out string?)"/>.</param>
    /// <param name="triplet">Triplet such as <c>57, 255, 20</c>.</param>
    /// <returns><see langword="true"/> when the color could be converted.</returns>
    public static bool TryGetRgbTriplet(string color, out string triplet)
    {
        triplet = string.Empty;
        string value = color.Trim();
        if (HexColorPattern().IsMatch(value))
        {
            string hex = value[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            }

            int r = int.Parse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(hex.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(hex.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            triplet = $"{r}, {g}, {b}";
            return true;
        }

        Match rgb = RgbIntegersPattern().Match(value);
        if (rgb.Success)
        {
            int[] channels = [.. Enumerable.Range(1, 3).Select(index => int.Parse(rgb.Groups[index].Value, CultureInfo.InvariantCulture))];
            if (channels.All(static channel => channel <= 255))
            {
                triplet = string.Join(", ", channels);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a validated color to <c>#rrggbb</c> for an HTML color input, when it is hexadecimal
    /// or plain <c>rgb()</c>; otherwise returns <see langword="null"/>.
    /// </summary>
    /// <param name="color">Color value.</param>
    public static string? ToHexColor(string? color)
    {
        if (color is null || !TryGetRgbTriplet(color, out string triplet))
        {
            return null;
        }

        int[] channels = [.. triplet.Split(", ").Select(static part => int.Parse(part, CultureInfo.InvariantCulture))];
        return $"#{channels[0]:x2}{channels[1]:x2}{channels[2]:x2}";
    }

    private static bool IsColor(string value) =>
        HexColorPattern().IsMatch(value)
        || ColorFunctionPattern().IsMatch(value)
        || NamedColors.Contains(value);

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColorPattern();

    // rgb()/rgba()/hsl()/hsla() with numbers, percents, angles, commas, spaces and the "/" alpha separator only.
    [GeneratedRegex(@"^(?:rgba?|hsla?)\(\s*[0-9.]+(?:deg|turn|%)?(?:\s*[,\s]\s*[0-9.]+%?){2}(?:\s*[,/]\s*[0-9.]+%?)?\s*\)$")]
    private static partial Regex ColorFunctionPattern();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*[,\s]\s*(\d{1,3})\s*[,\s]\s*(\d{1,3})\s*(?:[,/]\s*[0-9.]+%?\s*)?\)$")]
    private static partial Regex RgbIntegersPattern();

    [GeneratedRegex(@"^(?:0|-?(?:\d+|\d*\.\d+)(?:px|rem|em|%|vh|vw))$")]
    private static partial Regex LengthPattern();

    [GeneratedRegex(@"^(?:\d+|\d*\.\d+)(?:ms|s)(?:\s+(?:ease|linear|ease-in|ease-out|ease-in-out))?$")]
    private static partial Regex DurationPattern();

    // Each family is either a quoted name (no quotes, backslashes, semicolons, braces or angle brackets inside)
    // or an unquoted identifier sequence such as sans-serif or Segoe UI.
    [GeneratedRegex("""^(?:"[^"\\;{}<>()]{1,64}"|'[^'\\;{}<>()]{1,64}'|[A-Za-z][A-Za-z0-9 \-]{0,63})(?:\s*,\s*(?:"[^"\\;{}<>()]{1,64}"|'[^'\\;{}<>()]{1,64}'|[A-Za-z][A-Za-z0-9 \-]{0,63}))*$""")]
    private static partial Regex FontFamilyPattern();

    [GeneratedRegex(@"^-?(?:\d+|\d*\.\d+)$")]
    private static partial Regex NumberPattern();
}
