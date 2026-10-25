using HackerOs.AppSdk;
using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// One row/tile/node model shared across every <see cref="FileViewMode"/>. A <see cref="FileViewItem"/>
/// does not know which mode is currently rendering it — selection, rename, and icon resolution are
/// identical regardless of view mode. See
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md</c>.
/// </summary>
/// <remarks>
/// Skeleton for <c>FV-001</c>: properties are populated by the owning <see cref="FileView"/>; the
/// mutating methods below require that coordination and are not implemented yet.
/// </remarks>
public sealed class FileViewItem
{
    /// <summary>Initializes an item snapshot for one directory entry.</summary>
    public FileViewItem(VirtualPath fullPath, string fileName, bool isDirectory, FileSystemEntryMetadata metadata)
    {
        FullPath = fullPath;
        FileName = fileName;
        IsDirectory = isDirectory;
        Metadata = metadata;
    }

    /// <summary>Gets the entry's canonical virtual path.</summary>
    public VirtualPath FullPath { get; }

    /// <summary>Gets the entry's display name.</summary>
    public string FileName { get; }

    /// <summary>Gets whether this entry is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the entry's immutable filesystem metadata.</summary>
    public FileSystemEntryMetadata Metadata { get; }

    /// <summary>
    /// Gets or sets a per-item icon override, checked before <see cref="IShellIconProvider"/> is
    /// consulted at all. Set from outside <see cref="FileView"/> (e.g. from an <c>OnPathChange</c>
    /// handler) to repaint icons for application-specific file types the shell provider doesn't know
    /// about.
    /// </summary>
    public ShellIconDescriptor? IconOverride { get; set; }

    /// <summary>
    /// Gets or sets the name shown in place of <see cref="FileName"/>: the app's name of a <c>.desktop</c>
    /// shortcut (ADR 0160 §6). Set by the File Explorer's decoration of the items.
    /// </summary>
    internal string? DisplayName { get; set; }

    /// <summary>Gets or sets whether the item is drawn greyed: a shortcut to an app that is not installed (ADR 0152 §5).</summary>
    internal bool IsDimmed { get; set; }

    /// <summary>Gets the text under the icon: <see cref="DisplayName"/> when set, else the file name.</summary>
    internal string Label => DisplayName ?? FileName;

    /// <summary>Gets whether this item is currently selected.</summary>
    public bool IsSelected { get; internal set; }

    /// <summary>
    /// Gets whether this item is cut to the clipboard and waits for a paste (<see cref="FileView.CutPaths"/>):
    /// it is shown dimmed, like Nautilus does.
    /// </summary>
    public bool IsCut { get; internal set; }

    /// <summary>Gets whether this item is currently in inline rename mode.</summary>
    public bool IsRenaming { get; internal set; }

    /// <summary>
    /// Gets or sets the <see cref="FileView"/> instance that owns this item. Set by that instance when
    /// it builds its <c>Items</c> list; <see langword="null"/> for a standalone item (e.g. constructed
    /// directly in a test) not attached to any control.
    /// </summary>
    internal FileView? Owner { get; set; }

    /// <summary>Selects this item, raising <c>SelectionChanging</c>/<c>SelectionChanged</c> on the owning <see cref="FileView"/>.</summary>
    /// <param name="additive">When <see langword="true"/>, adds to the current selection instead of replacing it.</param>
    /// <exception cref="InvalidOperationException">This item is not attached to a <see cref="FileView"/>.</exception>
    public void Select(bool additive = false) => RequireOwner().SelectItemInternal(this, additive);

    /// <summary>Deselects this item.</summary>
    /// <exception cref="InvalidOperationException">This item is not attached to a <see cref="FileView"/>.</exception>
    public void Deselect() => RequireOwner().DeselectItemInternal(this);

    /// <summary>
    /// Puts this item into inline rename mode: its label is replaced by an editable textbox in place.
    /// No dialog is shown.
    /// </summary>
    /// <exception cref="InvalidOperationException">This item is not attached to a <see cref="FileView"/>.</exception>
    public void Rename()
    {
        FileView owner = RequireOwner();
        if (!owner.CanRenameItems)
        {
            return;
        }

        IsRenaming = true;
        owner.NotifyItemRenamingStateChanged(this);
    }

    private FileView RequireOwner() =>
        Owner ?? throw new InvalidOperationException("This item is not attached to a FileView instance.");
}
