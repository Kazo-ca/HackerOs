using System.Text;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Simulation.Abstractions.Sounds;
using HackerOs.Theming.Abstractions;
using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Platform.Core.Apps.ThemePacks;

/// <summary>The parts a theme pack holds (ADR 0190 §1).</summary>
[Flags]
public enum ThemePackParts
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary><c>themes/*.theme</c>.</summary>
    Themes = 1,

    /// <summary><c>wallpapers/*.png|jpg|webp</c>.</summary>
    Wallpapers = 2,

    /// <summary><c>icons/</c>, an icon pack.</summary>
    Icons = 4,

    /// <summary><c>sounds/</c>, a sound theme named after the pack.</summary>
    Sounds = 8,

    /// <summary><c>cursors/</c>, a cursor theme.</summary>
    Cursors = 16
}

/// <summary>
/// A verified theme pack (<c>.hacktheme</c>, ADR 0190, ADR 0213 §1): a zip archive holding
/// <c>hackeros-theme-pack.json</c> and data files only, each listed with its SHA-256, each of an
/// accepted format (checked from its first bytes, never its name) and within its size limit.
/// Nothing in it is code: no SVG, no script, no remote address.
/// </summary>
public sealed class ThemePack
{
    /// <summary>Newest <c>schemaVersion</c> of <c>hackeros-theme-pack.json</c> this OS reads.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Largest archive accepted, and largest total once expanded, in bytes (30 MiB, ADR 0190 §3).</summary>
    public const long MaximumBytes = 30L * 1024 * 1024;

    /// <summary>Most files in one pack.</summary>
    public const int MaximumFiles = 2_000;

    /// <summary>Largest <c>hackeros-theme-pack.json</c>, in bytes.</summary>
    public const int MaximumManifestBytes = 256 * 1024;

    /// <summary>Largest wallpaper of a pack, in bytes (5 MiB, ADR 0190 §3).</summary>
    public const long MaximumWallpaperBytes = 5L * 1024 * 1024;

    /// <summary>Largest <c>preview.png</c>, in bytes.</summary>
    public const long MaximumPreviewBytes = 1024 * 1024;

    /// <summary>Most <c>.theme</c> files in one pack.</summary>
    public const int MaximumThemes = 32;

    /// <summary>Most wallpapers in one pack.</summary>
    public const int MaximumWallpapers = 32;

    private const int MaximumNameLength = 64;
    private const int MaximumDescriptionLength = 500;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private ThemePack(
        string id, SemanticVersion version, string name, string? author, string? description, ThemePackParts parts, IReadOnlyDictionary<string, byte[]> files)
    {
        Id = id;
        Version = version;
        Name = name;
        Author = author;
        Description = description;
        Parts = parts;
        Files = files;
    }

    /// <summary>Gets the pack id: an app-style id (<c>org.example.neon</c>), also the id of its sound theme.</summary>
    public string Id { get; }

    /// <summary>Gets the pack version.</summary>
    public SemanticVersion Version { get; }

    /// <summary>Gets the name shown by the Store.</summary>
    public string Name { get; }

    /// <summary>Gets its author, if it says.</summary>
    public string? Author { get; }

    /// <summary>Gets a one-paragraph description, if it says.</summary>
    public string? Description { get; }

    /// <summary>Gets the parts it holds; never <see cref="ThemePackParts.None"/>.</summary>
    public ThemePackParts Parts { get; }

    /// <summary>Gets every verified file by its archive path, <c>hackeros-theme-pack.json</c> included.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>Gets the total size of its files, shown in the approval window.</summary>
    public long SizeBytes => Files.Values.Sum(static file => (long)file.Length);

    /// <summary>Whether <paramref name="id"/> can name a pack: a valid app id that is also a valid sound theme id.</summary>
    /// <param name="id">Candidate id.</param>
    public static bool IsValidId(string? id) =>
        id is not null && AppPackageSource.IsValidAppId(id) && OsSoundEvents.IsValidThemeId(id);

    /// <summary>Reads and verifies a pack.</summary>
    /// <param name="archive">The <c>.hacktheme</c> bytes.</param>
    /// <exception cref="AppPackageException">The pack is malformed, altered or holds a refused file; the code says why.</exception>
    public static ThemePack Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.LongLength > MaximumBytes)
        {
            throw Refuse("too-large", $"The theme pack is larger than {MaximumBytes / (1024 * 1024)} MiB.");
        }

        Dictionary<string, byte[]> files = AppPackage.Extract(archive, MaximumBytes, MaximumFiles, "theme pack");
        if (!files.TryGetValue(ThemePackLayout.ManifestFileName, out byte[]? manifestBytes))
        {
            throw Refuse("no-description", $"The theme pack has no {ThemePackLayout.ManifestFileName}.");
        }

        Manifest manifest = ReadManifest(manifestBytes);
        foreach (string path in files.Keys.Where(static path => path != ThemePackLayout.ManifestFileName))
        {
            if (!manifest.Files.ContainsKey(path))
            {
                throw Refuse("unlisted-file", $"'{path}' is in the theme pack but not in {ThemePackLayout.ManifestFileName}.");
            }
        }

        foreach ((string path, string sha256) in manifest.Files)
        {
            if (!files.TryGetValue(path, out byte[]? bytes))
            {
                throw Refuse("missing-file", $"'{path}' is listed in {ThemePackLayout.ManifestFileName} but missing.");
            }

            if (!string.Equals(AppPackage.Sha256(bytes), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw Refuse("hash-mismatch", $"'{path}' does not match its SHA-256.");
            }
        }

        ThemePackParts parts = CheckContents(manifest.Id, files);
        if (parts == ThemePackParts.None)
        {
            throw Refuse("empty", "The theme pack holds no theme, wallpaper, icon, sound or cursor.");
        }

        return new ThemePack(manifest.Id, manifest.Version, manifest.Name, manifest.Author, manifest.Description, parts, files);
    }

    /// <summary>
    /// Checks every file against the rules of its folder (ADR 0213 §1) and returns the parts found.
    /// The rules are those the OS applies when it reads the same folders, so a pack the Store
    /// accepts is a pack every catalog shows.
    /// </summary>
    private static ThemePackParts CheckContents(string packId, Dictionary<string, byte[]> files)
    {
        ThemePackParts parts = ThemePackParts.None;
        int themes = 0;
        int wallpapers = 0;
        long iconBytes = 0;
        int icons = 0;
        foreach ((string path, byte[] bytes) in files.OrderBy(static file => file.Key, StringComparer.Ordinal))
        {
            string[] segments = path.Split('/');
            string fileName = segments[^1];
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (extension == ".svg" || extension == ".svgz")
            {
                throw Refuse("unexpected-file", $"'{path}': SVG can hold script and is refused (ADR 0190 §3).");
            }

            if (fileName != fileName.ToLowerInvariant())
            {
                throw Refuse("unexpected-file", $"'{path}': file names are lower case.");
            }

            switch (segments[0])
            {
                case ThemePackLayout.ManifestFileName when segments.Length == 1:
                    break;
                case ThemePackLayout.PreviewFileName when segments.Length == 1:
                    RequirePng(path, bytes, MaximumPreviewBytes);
                    break;
                case ThemePackLayout.ThemesFolder when segments.Length == 2 && extension == ThemeFile.Extension:
                    CheckTheme(path, bytes);
                    themes++;
                    parts |= ThemePackParts.Themes;
                    break;
                case ThemePackLayout.WallpapersFolder when segments.Length == 2 && WallpaperCatalog.HasSupportedExtension(fileName):
                    CheckWallpaper(path, bytes);
                    wallpapers++;
                    parts |= ThemePackParts.Wallpapers;
                    break;
                case ThemePackLayout.IconsFolder:
                    CheckIcon(path, segments, bytes);
                    if (segments.Length == 3)
                    {
                        iconBytes += bytes.Length;
                        icons++;
                    }

                    parts |= ThemePackParts.Icons;
                    break;
                case ThemePackLayout.SoundsFolder:
                    CheckSound(path, segments, bytes);
                    parts |= ThemePackParts.Sounds;
                    break;
                case ThemePackLayout.CursorsFolder when segments.Length == 2:
                    // The images are checked with the index below, at their hot spots.
                    if (fileName != CursorThemeFile.IndexFileName && extension != IconPackLibrary.ImageExtension)
                    {
                        throw Unexpected(path);
                    }

                    parts |= ThemePackParts.Cursors;
                    break;
                default:
                    throw Unexpected(path);
            }
        }

        if (themes > MaximumThemes || wallpapers > MaximumWallpapers)
        {
            throw Refuse("too-many-files", $"A theme pack holds at most {MaximumThemes} themes and {MaximumWallpapers} wallpapers.");
        }

        if (parts.HasFlag(ThemePackParts.Icons))
        {
            CheckIconPack(files, icons, iconBytes);
        }

        if (parts.HasFlag(ThemePackParts.Sounds))
        {
            CheckSoundTheme(packId, files);
        }

        if (parts.HasFlag(ThemePackParts.Cursors))
        {
            CheckCursorTheme(files);
        }

        return parts;
    }

    private static void CheckTheme(string path, byte[] bytes)
    {
        string content = ReadText(path, bytes, ThemeFile.MaxFileBytes);
        ThemeFileParseResult parsed = ThemeFile.Parse(content, ThemeOrigin.System, path);
        if (parsed.Theme is null || parsed.Diagnostics.Any(static diagnostic => diagnostic.IsError))
        {
            throw Refuse(
                "bad-file",
                $"'{path}' is not a valid theme: {string.Join(" ", parsed.Diagnostics.Where(static d => d.IsError).Select(static d => d.ToString()))}");
        }
    }

    private static void CheckWallpaper(string path, byte[] bytes)
    {
        if (bytes.LongLength > MaximumWallpaperBytes)
        {
            throw Refuse("file-too-large", $"'{path}' is larger than {MaximumWallpaperBytes / (1024 * 1024)} MiB.");
        }

        if (!WallpaperCatalog.TryDetectMediaType(bytes, out _))
        {
            throw Refuse("bad-file", $"'{path}' is not a PNG, JPEG or WebP image.");
        }
    }

    private static void CheckIcon(string path, string[] segments, byte[] bytes)
    {
        if (segments.Length == 2 && segments[1] == IconPackLibrary.ManifestFileName)
        {
            return;
        }

        if (segments.Length != 3
            || segments[1] is not (IconPack.AppsFolder or IconPack.CategoriesFolder or IconPack.FilesFolder)
            || !segments[2].EndsWith(IconPackLibrary.ImageExtension, StringComparison.Ordinal))
        {
            throw Unexpected(path);
        }

        RequirePng(path, bytes, IconPackLibrary.MaxImageBytes);
    }

    private static void CheckIconPack(Dictionary<string, byte[]> files, int images, long imageBytes)
    {
        string manifestPath = $"{ThemePackLayout.IconsFolder}/{IconPackLibrary.ManifestFileName}";
        if (!files.TryGetValue(manifestPath, out byte[]? manifest))
        {
            throw Refuse("bad-file", $"The icons/ folder needs its {manifestPath}.");
        }

        string content = ReadText(manifestPath, manifest, IconPackLibrary.MaxManifestBytes);
        if (!IconPackLibrary.TryParseManifest(content, "/" + ThemePackLayout.IconsFolder, IconPackOrigin.System, out _, out string? error))
        {
            throw Refuse("bad-file", $"{manifestPath}: {error}");
        }

        if (images > IconPackLibrary.MaxImages || imageBytes > IconPackLibrary.MaxPackBytes)
        {
            throw Refuse("too-large", $"The icon pack holds more than {IconPackLibrary.MaxImages} images or {IconPackLibrary.MaxPackBytes / (1024 * 1024)} MiB.");
        }
    }

    private static void CheckSound(string path, string[] segments, byte[] bytes)
    {
        string fileName = segments[^1];
        if (segments.Length == 2 && fileName == OsSoundEvents.IndexFileName)
        {
            return;
        }

        if (segments.Length is < 2 or > 3 || OsSoundEvents.GetMediaType(fileName) is null)
        {
            throw Unexpected(path);
        }

        if (bytes.LongLength > OsSoundEvents.MaxSoundFileBytes)
        {
            throw Refuse("file-too-large", $"'{path}' is larger than {OsSoundEvents.MaxSoundFileBytes / 1024} KiB.");
        }

        bool ogg = bytes.Length >= 4 && bytes[0] == 'O' && bytes[1] == 'g' && bytes[2] == 'g' && bytes[3] == 'S';
        bool mp3 = (bytes.Length >= 3 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
            || (bytes.Length >= 2 && bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0);
        bool expected = Path.GetExtension(fileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? mp3 : ogg;
        if (!expected)
        {
            throw Refuse("bad-file", $"'{path}' is not the audio format its name says.");
        }
    }

    private static void CheckSoundTheme(string packId, Dictionary<string, byte[]> files)
    {
        string indexPath = $"{ThemePackLayout.SoundsFolder}/{OsSoundEvents.IndexFileName}";
        if (!files.TryGetValue(indexPath, out byte[]? index))
        {
            throw Refuse("bad-file", $"The sounds/ folder needs its {indexPath}.");
        }

        SoundThemeIndex theme = SoundThemeIndex.Parse(packId, ReadText(indexPath, index, ThemeFile.MaxFileBytes));
        HashSet<string> searched = [.. theme.GetSearchDirectories("/" + ThemePackLayout.SoundsFolder).Select(static directory => directory.TrimStart('/'))];
        foreach (string path in files.Keys.Where(static path => path.StartsWith(ThemePackLayout.SoundsFolder + "/", StringComparison.Ordinal) && path != $"{ThemePackLayout.SoundsFolder}/{OsSoundEvents.IndexFileName}"))
        {
            string directory = path[..path.LastIndexOf('/')];
            if (!searched.Contains(directory))
            {
                throw Refuse("bad-file", $"'{path}' is in a folder {indexPath} does not list in Directories=.");
            }
        }
    }

    private static void CheckCursorTheme(Dictionary<string, byte[]> files)
    {
        string indexPath = $"{ThemePackLayout.CursorsFolder}/{CursorThemeFile.IndexFileName}";
        if (!files.TryGetValue(indexPath, out byte[]? index))
        {
            throw Refuse("bad-file", $"The cursors/ folder needs its {indexPath}.");
        }

        CursorThemeParseResult parsed = CursorThemeFile.Parse(ReadText(indexPath, index, CursorThemeFile.MaxIndexBytes), indexPath);
        if (parsed.Index is not { } theme || parsed.Diagnostics.Any(static diagnostic => diagnostic.IsError))
        {
            throw Refuse("bad-file", $"{indexPath} is not valid: {string.Join(" ", parsed.Diagnostics.Select(static d => d.ToString()))}");
        }

        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (CursorThemeEntry entry in theme.Entries)
        {
            string imagePath = $"{ThemePackLayout.CursorsFolder}/{entry.FileName}";
            if (!files.TryGetValue(imagePath, out byte[]? image)
                || !CursorThemeFile.TryValidateImage(image, entry.HotspotX, entry.HotspotY, out _))
            {
                throw Refuse("bad-file", $"{indexPath}: the image '{entry.FileName}' of '{entry.Name}' is missing or refused.");
            }

            used.Add(imagePath);
        }

        string? unused = files.Keys.FirstOrDefault(path =>
            path.StartsWith(ThemePackLayout.CursorsFolder + "/", StringComparison.Ordinal) && path != indexPath && !used.Contains(path));
        if (unused is not null)
        {
            throw Refuse("unexpected-file", $"'{unused}' is not named by {indexPath}.");
        }
    }

    private static void RequirePng(string path, byte[] bytes, long maximum)
    {
        if (bytes.LongLength > maximum)
        {
            throw Refuse("file-too-large", $"'{path}' is larger than {maximum / 1024} KiB.");
        }

        if (!bytes.AsSpan().StartsWith(PngSignature))
        {
            throw Refuse("bad-file", $"'{path}' is not a PNG image.");
        }
    }

    /// <summary>Reads a text file of the pack: UTF-8, within its limit, and without any remote address (ADR 0190 §3).</summary>
    private static string ReadText(string path, byte[] bytes, long maximum)
    {
        if (bytes.LongLength > maximum)
        {
            throw Refuse("file-too-large", $"'{path}' is larger than {maximum / 1024} KiB.");
        }

        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Refuse("bad-file", $"'{path}' is not UTF-8 text.");
        }

        if (ContainsRemoteAddress(text))
        {
            throw Refuse("remote-address", $"'{path}' names a remote address; a theme pack is offline data only.");
        }

        return text;
    }

    /// <summary>Whether a text names something to fetch: a URL (<c>https://</c>), a CSS <c>url(</c> or <c>@import</c>.</summary>
    /// <param name="text">The text.</param>
    internal static bool ContainsRemoteAddress(string text) =>
        text.Contains("://", StringComparison.Ordinal)
        || text.Contains("url(", StringComparison.OrdinalIgnoreCase)
        || text.Contains("@import", StringComparison.OrdinalIgnoreCase);

    private static Manifest ReadManifest(byte[] bytes)
    {
        string text = ReadText(ThemePackLayout.ManifestFileName, bytes, MaximumManifestBytes);
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            int schema = root.GetProperty("schemaVersion").GetInt32();
            if (schema < 1 || schema > SupportedSchemaVersion)
            {
                throw Refuse("unsupported", $"{ThemePackLayout.ManifestFileName} has schemaVersion {schema}; this OS reads {SupportedSchemaVersion}.");
            }

            string id = root.GetProperty("id").GetString() ?? string.Empty;
            string name = (root.GetProperty("name").GetString() ?? string.Empty).Trim();
            if (!IsValidId(id)
                || !SemanticVersion.TryParse(root.GetProperty("version").GetString(), out SemanticVersion version)
                || name.Length is 0 or > MaximumNameLength)
            {
                throw Refuse("bad-description", $"{ThemePackLayout.ManifestFileName} needs a valid id, a version and a name of 1 to {MaximumNameLength} characters.");
            }

            Dictionary<string, string> listed = new(StringComparer.Ordinal);
            foreach (JsonProperty file in root.GetProperty("files").EnumerateObject())
            {
                string hash = file.Value.GetString() ?? string.Empty;
                if (!AppPackage.IsSafeRelativePath(file.Name) || file.Name == ThemePackLayout.ManifestFileName || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                {
                    throw Refuse("bad-description", $"{ThemePackLayout.ManifestFileName} lists '{file.Name}' with an invalid path or SHA-256.");
                }

                listed[file.Name] = hash;
            }

            return new Manifest(
                id,
                version,
                name,
                Optional(root, "author", MaximumNameLength),
                Optional(root, "description", MaximumDescriptionLength),
                listed);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Refuse("bad-description", $"{ThemePackLayout.ManifestFileName} is not valid: {exception.Message}");
        }
    }

    private static string? Optional(JsonElement root, string property, int maximumLength) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            && value.GetString()?.Trim() is { Length: > 0 } text
            ? text.Length <= maximumLength ? text : text[..maximumLength]
            : null;

    private static AppPackageException Unexpected(string path) =>
        Refuse("unexpected-file", $"'{path}' is not a file a theme pack may hold (themes/, wallpapers/, icons/, sounds/, cursors/, preview.png).");

    private static AppPackageException Refuse(string reason, string message) => new($"themes.pack.{reason}", message);

    private sealed record Manifest(
        string Id, SemanticVersion Version, string Name, string? Author, string? Description, IReadOnlyDictionary<string, string> Files);
}
