using HackerOs.App.Abstractions;

namespace HackerOs.Platform.Core.ContextMenus;

/// <summary>One item a file menu acts on, as the matcher sees it.</summary>
/// <param name="Path">Absolute path.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="MediaType">Its media type, or <see langword="null"/> when unknown.</param>
public sealed record FileMenuTarget(string Path, bool IsDirectory, string? MediaType);

/// <summary>
/// The filters of ADR 0151 §4.9 and §7 applied to one registered entry before it is placed: scope,
/// types and selection. Pure, so the rules are tested without a menu or a file system.
/// </summary>
public static class FileContextMenuMatcher
{
    /// <summary>The media type of a folder, the only type a folder matches besides <c>*</c>.</summary>
    public const string DirectoryMediaType = "inode/directory";

    /// <summary>
    /// Gets whether <paramref name="entry"/> belongs in a menu opened in <paramref name="scope"/> on
    /// <paramref name="targets"/>.
    /// </summary>
    /// <param name="entry">The registered entry.</param>
    /// <param name="scope">
    /// Where the menu opens: <see cref="AppContextMenus.FileScope"/> or <see cref="AppContextMenus.DirectoryScope"/>
    /// on items, <see cref="AppContextMenus.BackgroundScope"/> or <see cref="AppContextMenus.DesktopScope"/> in the empty space.
    /// </param>
    /// <param name="targets">The items the menu acts on; empty in the empty space.</param>
    /// <returns>Whether the entry is shown.</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item>On items, each target must be accepted: a file by the <c>file</c> scope and the types, a
    /// folder by the <c>directory</c> scope (and, when types are declared, <c>*</c> or
    /// <c>inode/directory</c>). A mixed selection therefore needs both scopes.</item>
    /// <item>The desktop's empty space is the background menu plus the desktop's own (ADR 0152): an
    /// entry of the <c>background</c> scope shows there too, one of the <c>desktop</c> scope only there.</item>
    /// <item><c>single</c> hides the entry when several items are selected.</item>
    /// </list>
    /// </remarks>
    public static bool Matches(AppContextMenuEntryManifest entry, string scope, IReadOnlyList<FileMenuTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(targets);
        IReadOnlyList<string> scopes = entry.Scopes;
        if (scope is AppContextMenus.BackgroundScope or AppContextMenus.DesktopScope)
        {
            return scopes.Contains(scope, StringComparer.Ordinal)
                || (scope == AppContextMenus.DesktopScope && scopes.Contains(AppContextMenus.BackgroundScope, StringComparer.Ordinal));
        }

        if (targets.Count == 0 || (targets.Count > 1 && entry.Selection == AppContextMenus.SelectionSingle))
        {
            return false;
        }

        foreach (FileMenuTarget target in targets)
        {
            bool accepted = target.IsDirectory
                ? scopes.Contains(AppContextMenus.DirectoryScope, StringComparer.Ordinal)
                    && (entry.Types is null || entry.Types.Any(static type => type is "*" or DirectoryMediaType))
                : scopes.Contains(AppContextMenus.FileScope, StringComparer.Ordinal)
                    && (entry.Types is null || entry.Types.Any(type => TypeMatches(type, target)));
            if (!accepted)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether the screen filter of ADR 0218 §2 lets <paramref name="entry"/> appear with the
    /// screens open now: <c>single</c> with screen 1 alone, <c>multiple</c> with two screens or more,
    /// <c>screen: n</c> while screen n is open. An unknown filter hides the entry.
    /// </summary>
    /// <param name="entry">The registered entry.</param>
    /// <param name="openScreens">The numbers of the open screens; screen 1 is counted even when absent.</param>
    public static bool MatchesScreens(AppContextMenuEntryManifest entry, IReadOnlySet<int> openScreens)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(openScreens);
        int count = openScreens.Count + (openScreens.Contains(1) ? 0 : 1);
        bool countMatches = entry.Screens switch
        {
            null => true,
            AppContextMenus.ScreensSingle => count == 1,
            AppContextMenus.ScreensMultiple => count > 1,
            _ => false
        };
        return countMatches && (entry.Screen is not { } screen || openScreens.Contains(screen));
    }

    /// <summary>Gets whether one declared type accepts a file: <c>*</c>, its extension, its media type or its family (<c>image/*</c>).</summary>
    /// <param name="type">The declared type, lower-case.</param>
    /// <param name="target">The file.</param>
    public static bool TypeMatches(string type, FileMenuTarget target)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(target);
        if (type == "*")
        {
            return true;
        }

        if (type.StartsWith('.'))
        {
            string name = target.Path[(target.Path.LastIndexOf('/') + 1)..];
            return name.Length > type.Length && name.EndsWith(type, StringComparison.OrdinalIgnoreCase);
        }

        if (target.MediaType is not { Length: > 0 } mediaType)
        {
            return false;
        }

        return type.EndsWith("/*", StringComparison.Ordinal)
            ? mediaType.StartsWith(type[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(type, mediaType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the title in the reader's language (ADR 0151 §2): the exact culture, then its parents
    /// (<c>fr-CA</c>, then <c>fr</c>), then English.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="culture">The reader's culture.</param>
    public static string TitleOf(AppContextMenuEntryManifest entry, System.Globalization.CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(culture);
        for (System.Globalization.CultureInfo current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (entry.Titles.FirstOrDefault(title => string.Equals(title.Culture, current.Name, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match.Text;
            }
        }

        return entry.Titles.FirstOrDefault(static title => title.Culture == AppContextMenus.EnglishCulture)?.Text ?? entry.Title;
    }
}
