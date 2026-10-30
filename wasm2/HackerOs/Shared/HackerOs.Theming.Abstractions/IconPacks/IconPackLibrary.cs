using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HackerOs.Theming.Abstractions.IconPacks;

/// <summary>
/// The catalog of icon packs (ADR 0203): folders holding an <c>icon-pack.json</c> and PNG images, found
/// in <c>/usr/share/icons/</c> and <c>~/.local/share/icons/</c>, as on a Linux desktop, and in the
/// <c>icons/</c> folder of the theme packs of ADR 0190. The selected pack is the <c>iconPackId</c> key
/// of the OS settings document <c>icons</c>; <see cref="BuiltInId"/> keeps the built-in icons.
/// </summary>
/// <remarks>
/// A pack is data only (ADR 0190 §3): PNG images, no SVG, no script, no remote address. Sizes are
/// bounded so a pack cannot fill the memory of the shell: <see cref="MaxImageBytes"/> per image,
/// <see cref="MaxPackBytes"/> and <see cref="MaxImages"/> per pack. An id already taken by a pack read
/// before is refused: a pack never hides another one, as for themes (ADR 0103).
/// </remarks>
public static partial class IconPackLibrary
{
    /// <summary>Folder of the icon packs installed for every user.</summary>
    public const string SystemDirectory = "/usr/share/icons";

    /// <summary>Folder of the theme packs installed for every user (ADR 0190 §2); their <c>icons/</c> folder is a pack.</summary>
    public const string SystemThemePackDirectory = "/usr/share/hackeros/theme-packs";

    /// <summary>Name of the file that makes a folder an icon pack.</summary>
    public const string ManifestFileName = "icon-pack.json";

    /// <summary>Folder of a theme pack that holds its icons.</summary>
    public const string ThemePackIconsFolder = "icons";

    /// <summary>The only image format of a pack.</summary>
    public const string ImageExtension = ".png";

    /// <summary>Id of the built-in icons, the default value of <see cref="PackIdKey"/>. No pack may take it.</summary>
    public const string BuiltInId = "builtin";

    /// <summary>Id of the OS settings document that names the selected pack (<c>/etc/hackeros/icons.config</c>, <c>~/.config/hackeros/icons.config</c>).</summary>
    public const string SettingsDocumentId = "icons";

    /// <summary>Key of the selected pack's id in <see cref="SettingsDocumentId"/>.</summary>
    public const string PackIdKey = "iconPackId";

    /// <summary>Largest accepted image, in bytes (256 KiB).</summary>
    public const int MaxImageBytes = 256 * 1024;

    /// <summary>Largest accepted pack, all images together, in bytes (8 MiB).</summary>
    public const int MaxPackBytes = 8 * 1024 * 1024;

    /// <summary>Most images read from one pack.</summary>
    public const int MaxImages = 1024;

    /// <summary>Largest accepted <c>icon-pack.json</c>, in bytes.</summary>
    public const int MaxManifestBytes = 16 * 1024;

    private const int MaxNameLength = 64;
    private const int MaxDescriptionLength = 200;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly string[] ImageFolders = [IconPack.AppsFolder, IconPack.CategoriesFolder, IconPack.FilesFolder];

    /// <summary>Gets the folder of a user's own icon packs.</summary>
    /// <param name="homeDirectory">The user's home, such as <c>/home/alice</c>.</param>
    public static string GetUserDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return $"{homeDirectory.TrimEnd('/')}/.local/share/icons";
    }

    /// <summary>Gets the folder of a user's own theme packs (ADR 0190 §2).</summary>
    /// <param name="homeDirectory">The user's home.</param>
    public static string GetUserThemePackDirectory(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return $"{homeDirectory.TrimEnd('/')}/.local/share/hackeros/theme-packs";
    }

    /// <summary>Whether <paramref name="id"/> is a valid pack id: 1 to 64 lower case letters, digits, <c>.</c>, <c>-</c> or <c>_</c>, not <see cref="BuiltInId"/>.</summary>
    /// <param name="id">Candidate id.</param>
    public static bool IsValidId(string? id) =>
        id is not null && IdPattern().IsMatch(id) && !string.Equals(id, BuiltInId, StringComparison.Ordinal);

    /// <summary>
    /// Finds every icon pack the caller can read: <c>/usr/share/icons/*</c>, then
    /// <c>/usr/share/hackeros/theme-packs/*/icons</c>, then the same two folders of the user's home.
    /// A missing folder, or a folder without <c>icon-pack.json</c>, is skipped without a warning.
    /// </summary>
    /// <param name="files">The caller's file access.</param>
    /// <param name="homeDirectory">The session user's home, or <see langword="null"/> to read the system folders only.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<IconPackListResult> ListAsync(
        IconPackFileAccess files,
        string? homeDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        List<(string Directory, bool IsThemePack, IconPackOrigin Origin)> roots =
        [
            (SystemDirectory, false, IconPackOrigin.System),
            (SystemThemePackDirectory, true, IconPackOrigin.System)
        ];
        if (!string.IsNullOrWhiteSpace(homeDirectory))
        {
            roots.Add((GetUserDirectory(homeDirectory), false, IconPackOrigin.User));
            roots.Add((GetUserThemePackDirectory(homeDirectory), true, IconPackOrigin.User));
        }

        List<IconPackInfo> packs = [];
        List<string> diagnostics = [];
        foreach ((string directory, bool isThemePack, IconPackOrigin origin) in roots)
        {
            IReadOnlyList<string> folders;
            try
            {
                folders = await files.ListDirectories(directory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Nobody installed a pack there yet: the normal case, not a warning.
                continue;
            }

            foreach (string folder in folders.Order(StringComparer.Ordinal))
            {
                string root = isThemePack ? $"{folder.TrimEnd('/')}/{ThemePackIconsFolder}" : folder.TrimEnd('/');
                IconPackInfo? info = await ReadManifestAsync(files, root, origin, diagnostics, cancellationToken).ConfigureAwait(false);
                if (info is null)
                {
                    continue;
                }

                if (packs.FirstOrDefault(existing => existing.Id == info.Id) is { } previous)
                {
                    diagnostics.Add($"{root}: the id '{info.Id}' is already used by {previous.RootPath}; the pack is ignored.");
                    continue;
                }

                packs.Add(info);
            }
        }

        return new IconPackListResult(packs, diagnostics);
    }

    /// <summary>
    /// Reads the images of one pack (ADR 0203 §1): the <c>.png</c> files of <c>apps/</c>, <c>categories/</c>
    /// and <c>files/</c>. A file of another format, too large, not a real PNG or past the pack's budget is
    /// refused with a warning; the rest of the pack still applies.
    /// </summary>
    /// <param name="info">The pack, as <see cref="ListAsync"/> found it.</param>
    /// <param name="files">The caller's file access.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public static async Task<IconPackLoadResult> LoadAsync(
        IconPackInfo info,
        IconPackFileAccess files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(files);
        Dictionary<string, string> images = new(StringComparer.Ordinal);
        List<string> diagnostics = [];
        long total = 0;
        foreach (string folder in ImageFolders)
        {
            IReadOnlyList<string> paths;
            try
            {
                paths = await files.ListFiles($"{info.RootPath}/{folder}", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A pack may cover only apps, or only files.
                continue;
            }

            foreach (string path in paths.Order(StringComparer.Ordinal))
            {
                string name = path[(path.LastIndexOf('/') + 1)..];
                if (!ImageNamePattern().IsMatch(name))
                {
                    diagnostics.Add($"{path}: only lower case .png images are read (no SVG, ADR 0190 §3).");
                    continue;
                }

                if (images.Count >= MaxImages)
                {
                    diagnostics.Add($"{path}: the pack has more than {MaxImages} images; the rest is ignored.");
                    return new IconPackLoadResult(new IconPack(info, images), diagnostics);
                }

                byte[] bytes;
                try
                {
                    bytes = await files.ReadAllBytes(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    diagnostics.Add($"{path}: cannot be read ({exception.Message}).");
                    continue;
                }

                if (bytes.Length > MaxImageBytes)
                {
                    diagnostics.Add($"{path}: larger than {MaxImageBytes / 1024} KiB.");
                    continue;
                }

                if (!IsPng(bytes))
                {
                    diagnostics.Add($"{path}: not a PNG image.");
                    continue;
                }

                if (total + bytes.Length > MaxPackBytes)
                {
                    diagnostics.Add($"{path}: the pack is larger than {MaxPackBytes / (1024 * 1024)} MiB; the rest is ignored.");
                    return new IconPackLoadResult(new IconPack(info, images), diagnostics);
                }

                total += bytes.Length;
                images[$"{folder}/{name}"] = "data:image/png;base64," + Convert.ToBase64String(bytes);
            }
        }

        return new IconPackLoadResult(new IconPack(info, images), diagnostics);
    }

    /// <summary>Parses an <c>icon-pack.json</c>.</summary>
    /// <param name="content">The file's content.</param>
    /// <param name="rootPath">The pack's folder.</param>
    /// <param name="origin">Which folder it came from.</param>
    /// <param name="info">The pack's identity when valid.</param>
    /// <param name="error">Why it was refused otherwise.</param>
    /// <returns><see langword="true"/> when the manifest is valid.</returns>
    public static bool TryParseManifest(string content, string rootPath, IconPackOrigin origin, out IconPackInfo? info, out string? error)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        info = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "the manifest is not a JSON object.";
                return false;
            }

            string? id = ReadString(root, "id");
            if (!IsValidId(id))
            {
                error = $"'id' must be 1 to 64 lower case letters, digits, '.', '-' or '_', and not '{BuiltInId}'.";
                return false;
            }

            string? name = ReadString(root, "name")?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength)
            {
                error = $"'name' is required and holds at most {MaxNameLength} characters.";
                return false;
            }

            string? description = ReadString(root, "description")?.Trim();
            if (description is { Length: > MaxDescriptionLength })
            {
                description = description[..MaxDescriptionLength];
            }

            info = new IconPackInfo(
                id!,
                name,
                Shorten(ReadString(root, "author")),
                Shorten(ReadString(root, "version")),
                description,
                rootPath.TrimEnd('/'),
                origin);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            error = $"invalid JSON ({exception.Message}).";
            return false;
        }
    }

    private static async Task<IconPackInfo?> ReadManifestAsync(
        IconPackFileAccess files,
        string root,
        IconPackOrigin origin,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        string path = $"{root}/{ManifestFileName}";
        byte[] bytes;
        try
        {
            bytes = await files.ReadAllBytes(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A folder of /usr/share/icons without the manifest is not an icon pack.
            return null;
        }

        if (bytes.Length > MaxManifestBytes)
        {
            diagnostics.Add($"{path}: larger than {MaxManifestBytes / 1024} KiB.");
            return null;
        }

        if (!TryParseManifest(Encoding.UTF8.GetString(bytes), root, origin, out IconPackInfo? info, out string? error))
        {
            diagnostics.Add($"{path}: {error}");
            return null;
        }

        return info;
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Shorten(string? value)
    {
        string? trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length > PngSignature.Length && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*\.png$")]
    private static partial Regex ImageNamePattern();
}
