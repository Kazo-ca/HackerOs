namespace HackerOs.Theming.Abstractions;

/// <summary>
/// How a wallpaper image covers a screen (ADR 0202 §3). Written in lower case in
/// <c>appearance.config</c> (<c>wallpaperFit = fill</c>).
/// </summary>
public enum WallpaperFit
{
    /// <summary>Covers the whole screen, keeping the image's proportions and cropping what overflows (the default).</summary>
    Fill,

    /// <summary>Shows the whole image, keeping its proportions; the theme's background shows around it.</summary>
    Fit,

    /// <summary>Covers the whole screen, ignoring the image's proportions.</summary>
    Stretch,

    /// <summary>Shows the image at its own size, in the middle of the screen.</summary>
    Center,

    /// <summary>Repeats the image at its own size from the top left corner.</summary>
    Tile
}

/// <summary>A wallpaper shipped with the OS as a static file of the theming library (ADR 0202 §2).</summary>
/// <param name="Id">Identifier, written <c>builtin:&lt;id&gt;</c> in <c>appearance.config</c>.</param>
/// <param name="AssetPath">Address of the image, relative to the page's base address.</param>
public sealed record BuiltInWallpaper(string Id, string AssetPath)
{
    /// <summary>Gets the value written in <c>appearance.config</c> to choose this wallpaper.</summary>
    public string Value => WallpaperCatalog.BuiltInPrefix + Id;
}

/// <summary>
/// The wallpapers of the OS (ADR 0202): the built-in images, the folders the catalog reads, and the
/// "data only" rules every image follows (PNG, JPEG or WebP, never SVG, size limit, ADR 0190 §3).
/// </summary>
/// <remarks>
/// A wallpaper value is one of: an empty string (the theme's own background), <c>builtin:&lt;id&gt;</c>
/// (a <see cref="BuiltIn"/> image), or the absolute virtual path of an image file.
/// </remarks>
public static class WallpaperCatalog
{
    /// <summary>Prefix of a built-in wallpaper value.</summary>
    public const string BuiltInPrefix = "builtin:";

    /// <summary>Folder of the built-in images, relative to the page's base address.</summary>
    public const string AssetDirectory = "_content/HackerOs.Theming.Blazor/wallpapers";

    /// <summary>Folder of the wallpapers an administrator installs for every user.</summary>
    public const string SystemDirectory = "/usr/share/hackeros/wallpapers";

    /// <summary>Folder of the theme packs installed for every user (ADR 0190 §2).</summary>
    public const string SystemThemePacksDirectory = "/usr/share/hackeros/theme-packs";

    /// <summary>Sub-folder of a theme pack that holds its wallpapers (ADR 0190 §1).</summary>
    public const string PackWallpapersFolder = "wallpapers";

    /// <summary>Largest image the OS shows as a wallpaper, in bytes (16 MiB).</summary>
    public const long MaxImageBytes = 16L * 1024 * 1024;

    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    /// <summary>Gets the wallpapers shipped with the OS, in display order.</summary>
    public static IReadOnlyList<BuiltInWallpaper> BuiltIn { get; } =
    [
        new("hacker-grid", $"{AssetDirectory}/hacker-grid.webp"),
        new("circuit", $"{AssetDirectory}/circuit.webp"),
        new("aurora", $"{AssetDirectory}/aurora.webp"),
        new("nebula", $"{AssetDirectory}/nebula.webp"),
    ];

    /// <summary>Gets every <c>wallpaperFit</c> value, in display order.</summary>
    public static IReadOnlyList<string> FitValues { get; } = [.. Enum.GetValues<WallpaperFit>().Select(FormatFit)];

    /// <summary>Gets the folder of a user's own wallpaper library: <c>~/.local/share/hackeros/wallpapers</c>.</summary>
    /// <param name="homeDirectory">The user's home, such as <c>/home/alice</c>.</param>
    public static string GetUserDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return $"{homeDirectory.TrimEnd('/')}/.local/share/hackeros/wallpapers";
    }

    /// <summary>Gets the folder of a user's own theme packs: <c>~/.local/share/hackeros/theme-packs</c> (ADR 0190 §2).</summary>
    /// <param name="homeDirectory">The user's home.</param>
    public static string GetUserThemePacksDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return $"{homeDirectory.TrimEnd('/')}/.local/share/hackeros/theme-packs";
    }

    /// <summary>Finds a built-in wallpaper from its <c>builtin:&lt;id&gt;</c> value.</summary>
    /// <param name="value">A wallpaper value.</param>
    /// <param name="wallpaper">The built-in wallpaper found.</param>
    public static bool TryGetBuiltIn(string? value, out BuiltInWallpaper wallpaper)
    {
        wallpaper = null!;
        if (value is null || !value.StartsWith(BuiltInPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string id = value[BuiltInPrefix.Length..];
        BuiltInWallpaper? found = BuiltIn.FirstOrDefault(candidate => candidate.Id == id);
        if (found is null)
        {
            return false;
        }

        wallpaper = found;
        return true;
    }

    /// <summary>Whether a file name has a wallpaper extension: <c>.png</c>, <c>.jpg</c>, <c>.jpeg</c> or <c>.webp</c>.</summary>
    /// <param name="path">File name or path.</param>
    public static bool HasSupportedExtension(string? path) =>
        !string.IsNullOrEmpty(path)
        && SupportedExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a value can be written in <c>appearance.config</c>: empty, a known built-in, or an
    /// absolute path to a PNG, JPEG or WebP file.
    /// </summary>
    /// <param name="value">A wallpaper value.</param>
    public static bool IsValidValue(string? value) =>
        string.IsNullOrEmpty(value)
        || TryGetBuiltIn(value, out _)
        || (value.StartsWith('/') && HasSupportedExtension(value) && !value.Contains('\n', StringComparison.Ordinal));

    /// <summary>
    /// Reads the media type from the first bytes of the file, never from its name: a file renamed
    /// <c>.png</c> that holds an SVG or a script is refused.
    /// </summary>
    /// <param name="header">The first bytes of the file (12 are enough).</param>
    /// <param name="mediaType"><c>image/png</c>, <c>image/jpeg</c> or <c>image/webp</c>.</param>
    public static bool TryDetectMediaType(ReadOnlySpan<byte> header, out string mediaType)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        ReadOnlySpan<byte> jpeg = [0xFF, 0xD8, 0xFF];

        if (header.StartsWith(png))
        {
            mediaType = "image/png";
            return true;
        }

        if (header.StartsWith(jpeg))
        {
            mediaType = "image/jpeg";
            return true;
        }

        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            mediaType = "image/webp";
            return true;
        }

        mediaType = string.Empty;
        return false;
    }

    /// <summary>Reads a <c>wallpaperFit</c> value; anything unknown is <see cref="WallpaperFit.Fill"/>.</summary>
    /// <param name="value">The value written in the file.</param>
    public static WallpaperFit ParseFit(string? value) => value switch
    {
        "fit" => WallpaperFit.Fit,
        "stretch" => WallpaperFit.Stretch,
        "center" => WallpaperFit.Center,
        "tile" => WallpaperFit.Tile,
        _ => WallpaperFit.Fill,
    };

    /// <summary>Writes a <c>wallpaperFit</c> value, in lower case.</summary>
    /// <param name="fit">The fit.</param>
    public static string FormatFit(WallpaperFit fit) => fit.ToString().ToLowerInvariant();
}
