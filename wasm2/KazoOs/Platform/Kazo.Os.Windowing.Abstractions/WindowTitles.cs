using System.Text;

namespace Kazo.Os.Windowing.Abstractions;

/// <summary>
/// Cleans a window title before the window manager shows it (ADR 0117 §1): the title bar, the
/// taskbar, the window switcher and the frame's accessible name all display the same cleaned text.
/// </summary>
public static class WindowTitles
{
    /// <summary>Longest title shown, in UTF-16 code units, the ellipsis included.</summary>
    public const int MaxLength = 256;

    /// <summary>Character that ends a title cut at <see cref="MaxLength"/>.</summary>
    public const char Ellipsis = '…';

    /// <summary>
    /// Removes control characters (line breaks, tabs, escape sequences that would break the title
    /// bar's single line), trims white space at both ends and cuts the result at
    /// <see cref="MaxLength"/> characters, ending with <see cref="Ellipsis"/> when cut.
    /// </summary>
    /// <param name="title">Title asked for by the app, or <see langword="null"/>.</param>
    /// <returns>The cleaned title; empty when nothing displayable remains.</returns>
    public static string Sanitize(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        StringBuilder builder = new(Math.Min(title.Length, MaxLength + 1));
        foreach (char character in title)
        {
            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length <= MaxLength)
        {
            return cleaned;
        }

        // Never cut a surrogate pair in half: the title would end with a broken character.
        int keep = MaxLength - 1;
        if (char.IsHighSurrogate(cleaned[keep - 1]))
        {
            keep--;
        }

        return string.Concat(cleaned.AsSpan(0, keep).TrimEnd(), Ellipsis.ToString());
    }

    /// <summary>
    /// Cleans <paramref name="title"/> and falls back to <paramref name="defaultTitle"/> (the app's
    /// localized name) when nothing displayable remains, as an empty or <see langword="null"/>
    /// title restores the default (ADR 0117 §1).
    /// </summary>
    /// <param name="title">Title asked for by the app.</param>
    /// <param name="defaultTitle">The window's default title.</param>
    public static string SanitizeOrDefault(string? title, string defaultTitle)
    {
        string cleaned = Sanitize(title);
        return cleaned.Length > 0 ? cleaned : Sanitize(defaultTitle);
    }
}
