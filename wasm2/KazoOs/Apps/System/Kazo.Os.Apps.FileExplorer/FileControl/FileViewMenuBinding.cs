using Kazo.Os.AppSdk;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Binds a <see cref="FileView"/> to its provider, the File Explorer: the OS context menu (ADR 0150) instead of its own
/// <c>MudMenu</c> (ADR 0155 §1), the decoration of shortcuts and the desktop mode (ADR 0160). The File Explorer, provider of <c>kazoos.file-view</c>, cascades
/// one around the view: every row, tile, tree node and the background then carry the attributes of
/// <c>Menus.Attach</c>, and the OS script opens the explorer's menu on them.
/// </summary>
/// <remarks>
/// Internal on purpose: this library leaves the SDK with the 2.0 (ADR 0149), and only its future owner,
/// the explorer, binds it. The properties are read on each render, so the explorer may change them
/// when its host's parameters change.
/// </remarks>
internal sealed class FileViewMenuBinding
{
    /// <summary>Gets or sets the attributes that open the menu on an item, or on the background for <see langword="null"/>.</summary>
    public Func<FileViewItem?, IReadOnlyDictionary<string, object>> Attach { get; set; } = static _ => Empty;

    /// <summary>Gets or sets whether F2 and the rename commands may rename an item in place.</summary>
    public bool AllowRename { get; set; } = true;

    /// <summary>Gets or sets whether the Delete key and the delete commands may remove items.</summary>
    public bool AllowDelete { get; set; } = true;

    /// <summary>
    /// Gets or sets what decorates each item when the folder is read: the name and icon of a
    /// <c>.desktop</c> shortcut, greyed when its app is missing (ADR 0160 §6). <see langword="null"/> leaves the items as listed.
    /// </summary>
    public Action<FileViewItem>? Decorate { get; set; }

    /// <summary>Gets or sets the desktop mode's state and callbacks (ADR 0160); <see langword="null"/> outside the desktop.</summary>
    public FileViewDesktopBinding? Desktop { get; set; }

    private static readonly IReadOnlyDictionary<string, object> Empty = new Dictionary<string, object>();
}
