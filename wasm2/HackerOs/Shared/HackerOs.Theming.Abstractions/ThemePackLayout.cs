namespace HackerOs.Theming.Abstractions;

/// <summary>
/// Where installed theme packs live and how their folders are named (ADR 0190 §1, §2; ADR 0213).
/// A pack installed from the Hack Store is unpacked as it is in
/// <c>/usr/share/hackeros/theme-packs/&lt;packId&gt;/</c> (every user) or
/// <c>~/.local/share/hackeros/theme-packs/&lt;packId&gt;/</c> (one user); the catalogs of themes,
/// wallpapers, icons, sounds and cursors read its sub-folders in place, so removing a pack is
/// removing one folder.
/// </summary>
public static class ThemePackLayout
{
    /// <summary>Extension of a theme pack archive.</summary>
    public const string Extension = ".hacktheme";

    /// <summary>Folder of the theme packs installed for every user.</summary>
    public const string SystemDirectory = "/usr/share/hackeros/theme-packs";

    /// <summary>Name of the pack description at the root of the archive and of the installed folder.</summary>
    public const string ManifestFileName = "hackeros-theme-pack.json";

    /// <summary>Name of the preview image the Hack Store shows.</summary>
    public const string PreviewFileName = "preview.png";

    /// <summary>Sub-folder of the <c>.theme</c> files (ADR 0103).</summary>
    public const string ThemesFolder = "themes";

    /// <summary>Sub-folder of the wallpaper images (ADR 0202).</summary>
    public const string WallpapersFolder = "wallpapers";

    /// <summary>Sub-folder that is an icon pack (ADR 0203).</summary>
    public const string IconsFolder = "icons";

    /// <summary>Sub-folder that is a freedesktop.org sound theme whose id is the pack id (ADR 0204).</summary>
    public const string SoundsFolder = "sounds";

    /// <summary>Sub-folder that is a cursor theme (ADR 0205).</summary>
    public const string CursorsFolder = "cursors";

    /// <summary>Gets the folder of a user's own theme packs.</summary>
    /// <param name="homeDirectory">The user's home, such as <c>/home/alice</c>.</param>
    public static string GetUserDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return $"{homeDirectory.TrimEnd('/')}/.local/share/hackeros/theme-packs";
    }

    /// <summary>
    /// Gets the folders that hold installed packs, system first: the order every catalog reads them
    /// in, so a system pack wins over a user pack of the same id.
    /// </summary>
    /// <param name="homeDirectory">The session user's home, or <see langword="null"/> for the system folder only.</param>
    public static IReadOnlyList<string> GetRoots(string? homeDirectory) =>
        string.IsNullOrWhiteSpace(homeDirectory) ? [SystemDirectory] : [SystemDirectory, GetUserDirectory(homeDirectory)];
}
