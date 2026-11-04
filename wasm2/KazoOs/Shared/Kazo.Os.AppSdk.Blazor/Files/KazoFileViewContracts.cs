using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Files;

namespace Kazo.Os.AppSdk.Blazor.Files;

/// <summary>How <see cref="KazoFileView"/> lays the folder out.</summary>
public enum KazoFileViewMode
{
    /// <summary>A sortable table: name, kind, size, date.</summary>
    Details = 0,

    /// <summary>A grid of large icons with their names.</summary>
    Icons = 1,

    /// <summary>A tree of folders and files that expands in place.</summary>
    Tree = 2,

    /// <summary>
    /// The desktop grid (ADR 0152 §2, ADR 0160 §2): each icon on a cell of its own, kept where the user
    /// put it (<see cref="KazoFileView.DesktopPositions"/>), with the Home and Trash system icons; a
    /// folder opens in a new explorer window. Transparent: the theme's wallpaper shows through.
    /// </summary>
    Desktop = 3
}

/// <summary>
/// What the file control does by itself for its host (ADR 0155 §3). Everything is on by default: an
/// app that shows <see cref="KazoFileView"/> gets the trash, the clipboard, the transfers and the
/// dialogs of the explorer without writing code. A host takes a feature away to remove its menu
/// entries, keys and gestures.
/// </summary>
[Flags]
public enum KazoFileViewFeatures
{
    /// <summary>Browse and open only.</summary>
    None = 0,

    /// <summary>Cut, Copy, Paste and Paste Into Folder: menu entries, Ctrl+X/C/V, host files pasted.</summary>
    Clipboard = 1,

    /// <summary>Delete (to the trash) and Delete permanently: menu entries, Delete and Shift+Delete.</summary>
    Delete = 2,

    /// <summary>Rename in place: menu entry and F2.</summary>
    Rename = 4,

    /// <summary>New Folder in the background menu.</summary>
    CreateFolder = 8,

    /// <summary>Drag and drop between folders, windows and from the host system.</summary>
    DragDrop = 16,

    /// <summary>The "Open with" submenu and "Choose another app…".</summary>
    OpenWith = 32,

    /// <summary>The Properties entry.</summary>
    Properties = 64,

    /// <summary>Every feature.</summary>
    All = Clipboard | Delete | Rename | CreateFolder | DragDrop | OpenWith | Properties
}

/// <summary>One file or folder as the file control shows it: a snapshot, taken when the folder was read.</summary>
/// <param name="Path">Absolute path.</param>
/// <param name="Name">Name shown.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Size">Size in bytes of a file; 0 for a folder.</param>
/// <param name="MediaType">Media type of a file (<c>text/plain</c>), <c>inode/directory</c> for a folder.</param>
/// <param name="ModifiedAt">When the content last changed.</param>
public sealed record KazoFileViewItem(
    string Path,
    string Name,
    bool IsDirectory,
    long Size,
    string MediaType,
    DateTimeOffset ModifiedAt);

/// <summary>
/// What a file menu is drawn for (ADR 0159 §5): set on <c>KazoMenuDefinition.FileMenu</c> by the
/// provider of the file control, so the OS adds the entries apps registered (ADR 0151) for these items.
/// </summary>
/// <param name="Scope">Where the menu opens: <see cref="FileMenuScope.File"/>, <see cref="FileMenuScope.Directory"/>, <see cref="FileMenuScope.Background"/> or <see cref="FileMenuScope.Desktop"/>.</param>
/// <param name="Items">The items the menu acts on; empty in the background.</param>
/// <param name="Directory">The folder the view shows.</param>
public sealed record KazoFileMenuContext(FileMenuScope Scope, IReadOnlyList<KazoFileViewItem> Items, string Directory);

/// <summary>Before the file control opens an item (double click, Enter, the menu's Open).</summary>
public sealed class KazoFileViewOpeningEventArgs
{
    /// <summary>Initializes the arguments.</summary>
    /// <param name="item">The item to open.</param>
    public KazoFileViewOpeningEventArgs(KazoFileViewItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }

    /// <summary>Gets the item to open.</summary>
    public KazoFileViewItem Item { get; }

    /// <summary>
    /// Gets or sets whether the host opened the item itself: the control then does nothing (no app
    /// launched for a file, no navigation into a folder).
    /// </summary>
    public bool Handled { get; set; }
}

/// <summary>After an item was renamed in place.</summary>
/// <param name="OldPath">Path before.</param>
/// <param name="NewPath">Path after.</param>
public sealed record KazoFileViewRenamedEventArgs(string OldPath, string NewPath);

/// <summary>After items were moved to the trash or deleted for good.</summary>
/// <param name="Paths">The items removed from their folder.</param>
/// <param name="Permanent">Whether they were deleted for good rather than moved to the trash.</param>
public sealed record KazoFileViewDeletedEventArgs(IReadOnlyList<string> Paths, bool Permanent);

/// <summary>
/// Before a file menu is drawn (ADR 0151 §1): the host may hide, disable or add entries for this
/// opening, or cancel it. Entries the host adds take an <c>OnClick</c> callback that receives the
/// right-clicked <see cref="KazoFileViewItem"/> (or <see langword="null"/> in the background);
/// <see cref="Items"/> holds every item the command applies to.
/// </summary>
public sealed class KazoFileMenuOpeningEventArgs
{
    /// <summary>Initializes the arguments.</summary>
    /// <param name="scope">Where the menu opens.</param>
    /// <param name="items">The items the menu acts on: the selection, or the item right-clicked alone.</param>
    /// <param name="directory">The folder the view shows.</param>
    /// <param name="menu">This opening's copy of the menu.</param>
    public KazoFileMenuOpeningEventArgs(FileMenuScope scope, IReadOnlyList<KazoFileViewItem> items, string directory, KazoMenuDefinition menu)
    {
        Scope = scope;
        Items = items ?? throw new ArgumentNullException(nameof(items));
        Directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Menu = menu ?? throw new ArgumentNullException(nameof(menu));
    }

    /// <summary>Gets where the menu opens: <see cref="FileMenuScope.File"/>, <see cref="FileMenuScope.Directory"/> or <see cref="FileMenuScope.Background"/>.</summary>
    public FileMenuScope Scope { get; }

    /// <summary>Gets the items the menu acts on; empty in the background.</summary>
    public IReadOnlyList<KazoFileViewItem> Items { get; }

    /// <summary>Gets the folder the view shows.</summary>
    public string Directory { get; }

    /// <summary>Gets this opening's copy of the menu (ids: <see cref="KazoFileMenuItems"/>).</summary>
    public KazoMenuDefinition Menu { get; }

    /// <summary>Gets or sets whether the menu stays closed.</summary>
    public bool Cancel { get; set; }
}

/// <summary>
/// The controller of the contract <c>hackeros.file-view</c> (ADR 0148 §2), handed by the OS once the
/// explorer's file control is ready in a <see cref="KazoFileView"/>. Every command acts with the
/// host's rights (ADR 0148 §4).
/// </summary>
public interface IHackFileView
{
    /// <summary>Gets the folder shown.</summary>
    string CurrentDirectory { get; }

    /// <summary>Gets the items shown, after the host's filter.</summary>
    IReadOnlyList<KazoFileViewItem> Items { get; }

    /// <summary>Gets the selected items.</summary>
    IReadOnlyList<KazoFileViewItem> SelectedItems { get; }

    /// <summary>Gets whether the session clipboard holds files to paste.</summary>
    bool CanPaste { get; }

    /// <summary>Gets whether a copy, a move, a deletion or a paste is still running.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Raised after the folder, the items, the selection, the clipboard or <see cref="IsBusy"/>
    /// changed, on the host's render thread: a toolbar re-reads the state.
    /// </summary>
    event EventHandler? StateChanged;

    /// <summary>Shows another folder.</summary>
    /// <param name="path">Absolute path of the folder.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="InvalidOperationException">The folder cannot be read; the message says why.</exception>
    Task NavigateAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads the folder again.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows the folder of <paramref name="path"/>, selects the item and scrolls it into view.</summary>
    /// <param name="path">Absolute path of the item.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Whether the item was found.</returns>
    Task<bool> RevealAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Clears the selection.</summary>
    void ClearSelection();

    /// <summary>Applies the host's <c>Filter</c> again to the items already read (a search box changed).</summary>
    void RefreshFilter();

    /// <summary>Gets whether <paramref name="command"/> can run now on the selection.</summary>
    /// <param name="command">An id of <see cref="KazoFileMenuItems"/>.</param>
    bool CanExecute(string command);

    /// <summary>
    /// Runs a menu command on the selection (the folder shown for <see cref="KazoFileMenuItems.Paste"/>,
    /// <see cref="KazoFileMenuItems.NewFolder"/> and <see cref="KazoFileMenuItems.Refresh"/>), as the
    /// menu would: a toolbar and the menu do the same thing. Does nothing when it cannot run.
    /// </summary>
    /// <param name="command">An id of <see cref="KazoFileMenuItems"/>.</param>
    Task ExecuteAsync(string command);

    /// <summary>Completes once no copy, move, deletion or paste is running (a window waits for it before it closes).</summary>
    Task WhenIdleAsync();
}
