using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Icons;

namespace Kazo.Os.Apps.FileExplorer.FileControl.Icons;

/// <summary>
/// Default <see cref="IShellIconProvider"/>: maps well-known extensions to <c>Kazo.Os.AppSdk.Icons</c>
/// vector icons, with a generic file/folder fallback. Registered as the DI default in
/// <c>EcosystemServiceCollectionExtensions</c> (<c>FV-007</c>); replaceable per-instance via
/// <see cref="FileView.IconProvider"/> and per-item via <see cref="FileViewItem.IconOverride"/>.
/// </summary>
public sealed class DefaultShellIconProvider : IShellIconProvider
{
    private static readonly IReadOnlyDictionary<string, string> ExtensionIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "file-text",
        [".md"] = "file-text",
        [".log"] = "file-text",
        [".pdf"] = "file-text",
        [".zip"] = "file-archive",
        [".tar"] = "file-archive",
        [".gz"] = "file-archive",
        [".7z"] = "file-archive",
        [".rar"] = "file-archive",
        [".exe"] = "app-window",
        [".dll"] = "app-window",
        [".json"] = "file-json",
        [".xml"] = "file-code",
        [".cs"] = "file-code",
        [".js"] = "file-code",
        [".ts"] = "file-code",
        [".html"] = "file-code",
        [".css"] = "file-code",
        [".razor"] = "file-code",
        [".png"] = "image",
        [".jpg"] = "image",
        [".jpeg"] = "image",
        [".gif"] = "image",
        [".svg"] = "image",
        [".bmp"] = "image",
        [".mp3"] = "music",
        [".wav"] = "music",
        [".mp4"] = "video",
        [".mov"] = "video",
    };

    /// <inheritdoc />
    public ShellIconDescriptor Resolve(FileViewIconRequest request)
    {
        if (request.IsDirectory)
        {
            return ShellIconDescriptor.Vector(IconLibrary.Lucide, "folder");
        }

        if (request.Extension is not null && ExtensionIcons.TryGetValue(request.Extension, out string? iconName))
        {
            return ShellIconDescriptor.Vector(IconLibrary.Lucide, iconName);
        }

        return ShellIconDescriptor.Vector(IconLibrary.Lucide, "file");
    }
}
