using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Apps.FileExplorer.FileControl.Icons;

/// <summary>
/// <see cref="IShellIconProvider"/> of a selected icon pack (ADR 0203 §4): the pack's image of a folder
/// or of a file's extension, else <see cref="DefaultShellIconProvider"/>'s built-in icon.
/// </summary>
public sealed class IconPackShellIconProvider : IShellIconProvider
{
    private readonly IShellIconProvider _fallback;

    /// <summary>Creates the provider.</summary>
    /// <param name="pack">The loaded pack.</param>
    /// <param name="fallback">Icons of the entries the pack does not cover; the built-in ones by default.</param>
    public IconPackShellIconProvider(IconPack pack, IShellIconProvider? fallback = null)
    {
        Pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _fallback = fallback ?? new DefaultShellIconProvider();
    }

    /// <summary>Gets the pack drawn.</summary>
    public IconPack Pack { get; }

    /// <inheritdoc />
    public ShellIconDescriptor Resolve(FileViewIconRequest request) =>
        Pack.FindFile(request.IsDirectory, request.Extension) is { } url
            ? ShellIconDescriptor.Png(url)
            : _fallback.Resolve(request);
}
