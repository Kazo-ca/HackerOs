namespace HackerOs.Theming.Abstractions.IconPacks;

/// <summary>
/// One loaded icon pack (ADR 0203 §1): its PNG images, already turned into <c>data:</c> URLs, and the
/// lookup rules every surface of the OS shares. A surface asks the pack first and draws its own
/// built-in icon when the pack has none.
/// </summary>
/// <remarks>
/// The images are named by convention, as in a freedesktop icon theme:
/// <list type="bullet">
/// <item><c>apps/&lt;appId&gt;.png</c>: one app (<c>apps/org.hackeros.terminal.png</c>);</item>
/// <item><c>categories/&lt;category&gt;.png</c>: every app of a category without its own image (<c>categories/developer.png</c>);</item>
/// <item><c>files/folder.png</c>, <c>files/file.png</c> and <c>files/&lt;extension&gt;.png</c> (<c>files/txt.png</c>).</item>
/// </list>
/// </remarks>
public sealed class IconPack
{
    /// <summary>Folder of the app images.</summary>
    public const string AppsFolder = "apps";

    /// <summary>Folder of the category images.</summary>
    public const string CategoriesFolder = "categories";

    /// <summary>Folder of the file and folder images.</summary>
    public const string FilesFolder = "files";

    /// <summary>Name (without <c>.png</c>) of the image of every folder.</summary>
    public const string FolderImageName = "folder";

    /// <summary>Name (without <c>.png</c>) of the image of a file whose extension has no image.</summary>
    public const string FileImageName = "file";

    private readonly IReadOnlyDictionary<string, string> _images;

    /// <summary>Creates a pack from its images.</summary>
    /// <param name="info">The pack's identity.</param>
    /// <param name="images">Relative path (<c>apps/org.hackeros.terminal.png</c>) to <c>data:</c> URL.</param>
    public IconPack(IconPackInfo info, IReadOnlyDictionary<string, string> images)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        ArgumentNullException.ThrowIfNull(images);
        _images = new Dictionary<string, string>(images, StringComparer.Ordinal);
    }

    /// <summary>Gets the pack's identity.</summary>
    public IconPackInfo Info { get; }

    /// <summary>Gets every image, by relative path.</summary>
    public IReadOnlyDictionary<string, string> Images => _images;

    /// <summary>Gets the image of one app, from <c>apps/&lt;appId&gt;.png</c> only.</summary>
    /// <param name="appId">The app id.</param>
    /// <returns>A <c>data:</c> URL, or <see langword="null"/> when the pack has no image for this app.</returns>
    public string? FindApp(string? appId) =>
        string.IsNullOrWhiteSpace(appId) ? null : Find(AppsFolder, appId.ToLowerInvariant());

    /// <summary>Gets the image of a category, from <c>categories/&lt;category&gt;.png</c>.</summary>
    /// <param name="category">The category (lower kebab case, such as <c>developer</c>).</param>
    /// <returns>A <c>data:</c> URL, or <see langword="null"/>.</returns>
    public string? FindCategory(string? category) =>
        string.IsNullOrWhiteSpace(category) ? null : Find(CategoriesFolder, category.ToLowerInvariant());

    /// <summary>
    /// Gets the image of an app as the Start menu, the desktop and the file view draw it: its own image,
    /// else its category's.
    /// </summary>
    /// <param name="appId">The app id.</param>
    /// <param name="category">Its category.</param>
    /// <returns>A <c>data:</c> URL, or <see langword="null"/>.</returns>
    public string? FindApp(string? appId, string? category) => FindApp(appId) ?? FindCategory(category);

    /// <summary>
    /// Gets the image of a file or a folder: <c>files/folder.png</c> for a folder; for a file
    /// <c>files/&lt;extension&gt;.png</c>, else <c>files/file.png</c>, so the pack's style is never mixed with
    /// the built-in icons for the files it covers.
    /// </summary>
    /// <param name="isDirectory">Whether the entry is a folder.</param>
    /// <param name="extension">Its extension, with or without the leading dot, in any case.</param>
    /// <returns>A <c>data:</c> URL, or <see langword="null"/>.</returns>
    public string? FindFile(bool isDirectory, string? extension)
    {
        if (isDirectory)
        {
            return Find(FilesFolder, FolderImageName);
        }

        string? name = extension?.TrimStart('.').ToLowerInvariant();
        return (string.IsNullOrEmpty(name) ? null : Find(FilesFolder, name)) ?? Find(FilesFolder, FileImageName);
    }

    private string? Find(string folder, string name) =>
        _images.TryGetValue($"{folder}/{name}{IconPackLibrary.ImageExtension}", out string? url) ? url : null;
}

/// <summary>A loaded pack and the warnings of its files.</summary>
/// <param name="Pack">The pack, with every accepted image.</param>
/// <param name="Diagnostics">One line per refused file.</param>
public sealed record IconPackLoadResult(IconPack Pack, IReadOnlyList<string> Diagnostics);
