using HackerOs.AppSdk;
using HackerOs.App.Abstractions;
using HackerOs.Apps.FileExplorer.FileControl.ContextMenu;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Apps.FileExplorer.FileControl.Events;

/// <summary>
/// Base type for every cancelable "-ing" event raised by <see cref="FileView"/> before it performs an
/// observable action. Setting <see cref="Cancel"/> stops the action from proceeding — this is the
/// literal <c>e.Cancel = true</c> interception point requested for the control. See
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md#cancelable-eventing-model</c>.
/// </summary>
public abstract class FileViewCancelEventArgs : EventArgs
{
    /// <summary>Gets or sets whether the pending action should be canceled.</summary>
    public bool Cancel { get; set; }
}

/// <summary>Raised before <see cref="FileView"/> navigates to a new directory.</summary>
public sealed class FileViewNavigatingEventArgs(VirtualPath from, VirtualPath to) : FileViewCancelEventArgs
{
    /// <summary>Gets the directory being navigated away from.</summary>
    public VirtualPath From { get; } = from;

    /// <summary>Gets the directory being navigated to.</summary>
    public VirtualPath To { get; } = to;
}

/// <summary>Raised after <see cref="FileView"/> has navigated. Named to match the control's documented external API.</summary>
public sealed class FileViewPathChangedEventArgs(VirtualPath path) : EventArgs
{
    /// <summary>Gets the current directory.</summary>
    public VirtualPath Path { get; } = path;
}

/// <summary>Raised before an item is activated (double-click, Enter, or context-menu default action).</summary>
public sealed class FileViewOpeningEventArgs(FileViewItem item, FileViewFolderActivationMode mode) : FileViewCancelEventArgs
{
    /// <summary>Gets the item being activated.</summary>
    public FileViewItem Item { get; } = item;

    /// <summary>Gets the configured folder-activation mode in effect for this activation.</summary>
    public FileViewFolderActivationMode Mode { get; } = mode;
}

/// <summary>Raised after an item has been activated.</summary>
public sealed class FileViewOpenedEventArgs(FileViewItem item) : EventArgs
{
    /// <summary>Gets the item that was activated.</summary>
    public FileViewItem Item { get; } = item;
}

/// <summary>Raised before an inline rename is committed to the filesystem.</summary>
public sealed class FileViewRenamingEventArgs(FileViewItem item, string proposedName) : FileViewCancelEventArgs
{
    /// <summary>Gets the item being renamed.</summary>
    public FileViewItem Item { get; } = item;

    /// <summary>Gets the proposed new name.</summary>
    public string ProposedName { get; } = proposedName;
}

/// <summary>Raised after an inline rename has been committed.</summary>
public sealed class FileViewRenamedEventArgs(FileViewItem item, string previousName) : EventArgs
{
    /// <summary>Gets the renamed item.</summary>
    public FileViewItem Item { get; } = item;

    /// <summary>Gets the item's previous name.</summary>
    public string PreviousName { get; } = previousName;
}

/// <summary>Raised before a create action initiated through <see cref="FileView"/>'s own UI proceeds.</summary>
public sealed class FileViewCreatingEventArgs(VirtualPath parentDirectory, string name, FileSystemEntryKind kind) : FileViewCancelEventArgs
{
    /// <summary>Gets the directory the new entry will be created in.</summary>
    public VirtualPath ParentDirectory { get; } = parentDirectory;

    /// <summary>Gets the new entry's name.</summary>
    public string Name { get; } = name;

    /// <summary>Gets the kind of entry being created.</summary>
    public FileSystemEntryKind Kind { get; } = kind;
}

/// <summary>Raised after a create action has completed.</summary>
public sealed class FileViewCreatedEventArgs(FileViewItem item) : EventArgs
{
    /// <summary>Gets the created item.</summary>
    public FileViewItem Item { get; } = item;
}

/// <summary>Raised before a delete action initiated through <see cref="FileView"/>'s own UI proceeds.</summary>
public sealed class FileViewDeletingEventArgs(IReadOnlyList<FileViewItem> items) : FileViewCancelEventArgs
{
    /// <summary>Gets the items pending deletion.</summary>
    public IReadOnlyList<FileViewItem> Items { get; } = items;
}

/// <summary>
/// Hands a delete action initiated through <see cref="FileView"/>'s own UI (Delete key, Shift+Delete,
/// context menu) to the host, when the host supplied <see cref="FileView.DeleteRequested"/> — for
/// instance to move the items to the trash instead of deleting them (ADR 0052, section 5).
/// </summary>
public sealed class FileViewDeleteRequestedEventArgs(IReadOnlyList<FileViewItem> items, bool permanent) : EventArgs
{
    /// <summary>Gets the items the user asked to delete.</summary>
    public IReadOnlyList<FileViewItem> Items { get; } = items;

    /// <summary>
    /// Gets whether the user asked for a permanent deletion (Shift+Delete or "Delete permanently")
    /// rather than the default, reversible one.
    /// </summary>
    public bool Permanent { get; } = permanent;
}

/// <summary>Raised after a delete action has completed.</summary>
public sealed class FileViewDeletedEventArgs(IReadOnlyList<VirtualPath> paths) : EventArgs
{
    /// <summary>Gets the paths that were deleted.</summary>
    public IReadOnlyList<VirtualPath> Paths { get; } = paths;
}

/// <summary>Raised before a move (drag-drop move or inline rename) proceeds.</summary>
public sealed class FileViewMovingEventArgs(IReadOnlyList<FileViewItem> items, VirtualPath destination) : FileViewCancelEventArgs
{
    /// <summary>Gets the items being moved.</summary>
    public IReadOnlyList<FileViewItem> Items { get; } = items;

    /// <summary>Gets the destination directory.</summary>
    public VirtualPath Destination { get; } = destination;
}

/// <summary>Raised after a move has completed.</summary>
public sealed class FileViewMovedEventArgs(IReadOnlyList<FileViewItem> items, VirtualPath destination) : EventArgs
{
    /// <summary>Gets the moved items.</summary>
    public IReadOnlyList<FileViewItem> Items { get; } = items;

    /// <summary>Gets the destination directory.</summary>
    public VirtualPath Destination { get; } = destination;
}

/// <summary>Raised before a drag-drop copy (Ctrl-modified drop) proceeds.</summary>
public sealed class FileViewCopyingEventArgs(IReadOnlyList<FileViewItem> items, VirtualPath destination) : FileViewCancelEventArgs
{
    /// <summary>Gets the items being copied.</summary>
    public IReadOnlyList<FileViewItem> Items { get; } = items;

    /// <summary>Gets the destination directory.</summary>
    public VirtualPath Destination { get; } = destination;
}

/// <summary>Raised after a drag-drop copy has completed.</summary>
public sealed class FileViewCopiedEventArgs(IReadOnlyList<FileViewItem> items, VirtualPath destination) : EventArgs
{
    /// <summary>Gets the copied items.</summary>
    public IReadOnlyList<FileViewItem> Items { get; } = items;

    /// <summary>Gets the destination directory.</summary>
    public VirtualPath Destination { get; } = destination;
}

/// <summary>Raised before a selection change (programmatic or user-driven) takes effect.</summary>
public sealed class FileViewSelectionChangingEventArgs(IReadOnlyList<FileViewItem> proposedSelection) : FileViewCancelEventArgs
{
    /// <summary>Gets the selection that would result if not canceled.</summary>
    public IReadOnlyList<FileViewItem> ProposedSelection { get; } = proposedSelection;
}

/// <summary>Raised after the selection has changed.</summary>
public sealed class FileViewSelectionChangedEventArgs(IReadOnlyList<FileViewItem> selection) : EventArgs
{
    /// <summary>Gets the current selection.</summary>
    public IReadOnlyList<FileViewItem> Selection { get; } = selection;
}

/// <summary>
/// Raised when a context menu is about to open, after every matching
/// <see cref="IFileViewContextMenuProvider"/> has already customized <see cref="Items"/> — this is the
/// final, host-level adjustment point.
/// </summary>
public sealed class FileViewContextMenuOpeningEventArgs(
    FileViewContextMenuScope scope,
    FileViewItem? item,
    FileViewMenuItemCollection items) : EventArgs
{
    /// <summary>Gets which scope triggered this menu.</summary>
    public FileViewContextMenuScope Scope { get; } = scope;

    /// <summary>Gets the item that was right-clicked, or <see langword="null"/> for the background scope.</summary>
    public FileViewItem? Item { get; } = item;

    /// <summary>Gets the mutable menu item collection about to be shown.</summary>
    public FileViewMenuItemCollection Items { get; } = items;
}

/// <summary>What the user asked the clipboard to do through <see cref="FileView"/>'s keys or menus (ADR 0058, section 1).</summary>
public enum FileViewClipboardAction
{
    /// <summary>Ctrl+X or "Cut": put the items on the clipboard to move them at the next paste.</summary>
    Cut = 0,

    /// <summary>Ctrl+C or "Copy": put the items on the clipboard to copy them at the next paste.</summary>
    Copy = 1,

    /// <summary>Ctrl+V, "Paste" or "Paste Into Folder": paste the clipboard into <see cref="FileViewClipboardRequestedEventArgs.Destination"/>.</summary>
    Paste = 2
}

/// <summary>
/// Hands a cut, copy or paste initiated through <see cref="FileView"/>'s own UI to the host, which
/// owns the clipboard (<c>HackClipboard</c>) and runs the transfer. Supplied only when the host set
/// <see cref="FileView.ClipboardRequested"/>.
/// </summary>
/// <param name="action">Cut, copy or paste.</param>
/// <param name="items">Items to cut or copy (the selection); empty for a paste.</param>
/// <param name="destination">Directory to paste into: the current one, or the folder the menu was opened on.</param>
public sealed class FileViewClipboardRequestedEventArgs(
    FileViewClipboardAction action, IReadOnlyList<FileViewItem> items, VirtualPath destination) : EventArgs
{
    /// <summary>Gets the requested action.</summary>
    public FileViewClipboardAction Action { get; } = action;

    /// <summary>Gets the items to cut or copy.</summary>
    public IReadOnlyList<FileViewItem> Items { get; } = items;

    /// <summary>Gets the directory a paste targets.</summary>
    public VirtualPath Destination { get; } = destination;
}

/// <summary>One HackerOS entry carried by a drag (from this view, another window, or another app's <see cref="FileView"/>).</summary>
/// <param name="Path">Absolute path of the dragged entry.</param>
/// <param name="IsDirectory">Whether the entry is a directory.</param>
public sealed record FileViewDraggedEntry(VirtualPath Path, bool IsDirectory);

/// <summary>
/// Hands a drop to the host (ADR 0058, section 1): HackerOS entries dragged from any
/// <see cref="FileView"/>, and/or files dragged from the host system, dropped on a folder, on the
/// background of the view, on a tree node, on a breadcrumb segment or on a <c>FileViewDropZone</c>; also
/// raised, as a copy into the current directory, for files pasted (Ctrl+V) from the host clipboard (ADR 0065).
/// </summary>
/// <param name="destination">Target directory, or <see langword="null"/> for a drop zone with no directory (the Trash).</param>
/// <param name="entries">Dragged HackerOS entries, already checked to exist; never the destination itself.</param>
/// <param name="hostFiles">Files dropped from the host system; read only when the host imports them.</param>
/// <param name="copy">Whether Ctrl was held (copy); otherwise a move.</param>
public sealed class FileViewDropRequestedEventArgs(
    VirtualPath? destination,
    IReadOnlyList<FileViewDraggedEntry> entries,
    IReadOnlyList<HackerOs.AppSdk.IO.HackHostFile> hostFiles,
    bool copy) : EventArgs
{
    /// <summary>Gets the target directory, or <see langword="null"/> for a drop zone without one.</summary>
    public VirtualPath? Destination { get; } = destination;

    /// <summary>Gets the dragged HackerOS entries.</summary>
    public IReadOnlyList<FileViewDraggedEntry> Entries { get; } = entries;

    /// <summary>Gets the files dropped from the host system.</summary>
    public IReadOnlyList<HackerOs.AppSdk.IO.HackHostFile> HostFiles { get; } = hostFiles;

    /// <summary>Gets whether Ctrl was held: copy instead of move.</summary>
    public bool Copy { get; } = copy;

    /// <summary>
    /// Gets the apps dragged from the Start menu (ADR 0160 §6): the host creates a <c>.desktop</c>
    /// shortcut to each in <see cref="Destination"/>.
    /// </summary>
    public IReadOnlyList<string> AppIds { get; init; } = [];

    /// <summary>Gets whether the drop carries nothing usable.</summary>
    public bool IsEmpty => Entries.Count == 0 && HostFiles.Count == 0 && AppIds.Count == 0;

    /// <summary>Key under which <c>FileView.razor.js</c> keeps the host File objects until the drop is handled.</summary>
    internal int HostFileToken { get; init; }
}
