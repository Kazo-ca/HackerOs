using HackerOs.AppSdk;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Icons;

namespace HackerOs.Apps.FileExplorer.FileControl.Icons;

/// <summary>
/// Identifies which rendering strategy a <see cref="ShellIconDescriptor"/> uses, so a renderer
/// (<see cref="ShellIcon"/>) never needs a caller-supplied branch to draw it.
/// </summary>
public enum ShellIconKind
{
    /// <summary>An inline SVG icon resolved through <c>HackerOs.AppSdk.Icons</c>' <see cref="IIconCatalog"/>.</summary>
    Vector,

    /// <summary>A raster image referenced by asset path or data URI.</summary>
    Png,

    /// <summary>A host-defined rendering not covered by <see cref="Vector"/> or <see cref="Png"/>.</summary>
    Custom
}

/// <summary>
/// Describes one resolved icon, independent of whether it came from the vector icon system or a raster
/// image, so every consumer (<see cref="FileView"/>, <see cref="ShellIcon"/>, per-item overrides) shares
/// one type. See
/// <c>doc/en/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md#icon-resolution-ishelliconprovider</c>.
/// </summary>
/// <param name="Kind">Which rendering strategy applies.</param>
/// <param name="LibraryOrPath">For <see cref="ShellIconKind.Vector"/>, the <see cref="IconLibrary"/> name; for <see cref="ShellIconKind.Png"/>, the asset path or URI.</param>
/// <param name="Name">For <see cref="ShellIconKind.Vector"/>, the icon's lookup name within its library.</param>
public sealed record ShellIconDescriptor(ShellIconKind Kind, string? LibraryOrPath, string? Name)
{
    /// <summary>Creates a vector-icon descriptor resolved through the shared icon catalog.</summary>
    public static ShellIconDescriptor Vector(IconLibrary library, string name) =>
        new(ShellIconKind.Vector, library.ToString(), name);

    /// <summary>Creates a raster-image descriptor from an asset path or data URI.</summary>
    public static ShellIconDescriptor Png(string assetPathOrUri) =>
        new(ShellIconKind.Png, assetPathOrUri, null);
}

/// <summary>Describes the entry a <see cref="IShellIconProvider"/> is being asked to resolve an icon for.</summary>
/// <param name="Path">Canonical virtual path of the entry.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
/// <param name="Extension">The entry's lowercase extension including the leading dot, when it has one.</param>
/// <param name="MediaType">The entry's detected media type, when known.</param>
public readonly record struct FileViewIconRequest(
    VirtualPath Path,
    bool IsDirectory,
    string? Extension,
    string? MediaType);

/// <summary>
/// Resolves the icon shown for one filesystem entry. <see cref="FileView"/> never decides icons itself —
/// it always asks an <see cref="IShellIconProvider"/> (the confirmed "icon retrieval is the Shell's
/// responsibility" decision), checking <see cref="FileViewItem.IconOverride"/> first. The default
/// implementation (<c>FV-007</c>) maps extensions to <c>HackerOs.AppSdk.Icons</c> vector icons; a future,
/// separately scoped implementation could resolve real host-OS icons behind this same interface with no
/// <see cref="FileView"/> change.
/// </summary>
public interface IShellIconProvider
{
    /// <summary>Resolves the icon for one entry.</summary>
    ShellIconDescriptor Resolve(FileViewIconRequest request);
}
