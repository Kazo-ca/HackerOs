using HackerOs.AppSdk;
using HackerOs.Apps.FileExplorer.FileControl.Icons;

namespace HackerOs.Apps.FileExplorer.FileControl.ContextMenu;

/// <summary>
/// Identifies which right-click surface a <see cref="IFileViewContextMenuProvider"/> customizes. See
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md#context-menu-customization</c>.
/// </summary>
public enum FileViewContextMenuScope
{
    /// <summary>Right-click on empty space within the current directory.</summary>
    Background,

    /// <summary>Right-click on a directory item.</summary>
    Directory,

    /// <summary>Right-click on any file item, regardless of type.</summary>
    File,

    /// <summary>
    /// Right-click on a file item whose extension or media type matches
    /// <see cref="IFileViewContextMenuProvider.Matches"/>. Evaluated after <see cref="File"/> providers.
    /// </summary>
    FileType
}

/// <summary>One entry in a context menu.</summary>
/// <param name="Id">Stable identifier. Every default item (<c>"open"</c>, <c>"rename"</c>, <c>"delete"</c>, ...)
/// is given a documented, stable ID specifically so a provider can locate and insert relative to it.</param>
/// <param name="Label">Display label.</param>
/// <param name="Icon">Optional icon.</param>
/// <param name="OnActivate">Invoked when the item is chosen.</param>
/// <param name="IsSeparatorBefore">Whether a visual separator precedes this item.</param>
/// <param name="IsDisabled">Whether the item is shown greyed out and cannot be chosen (e.g. "Paste" with nothing to paste).</param>
public sealed record FileViewMenuItem(
    string Id,
    string Label,
    ShellIconDescriptor? Icon,
    Func<Task> OnActivate,
    bool IsSeparatorBefore = false,
    bool IsDisabled = false);

/// <summary>
/// An ordered, mutable collection of <see cref="FileViewMenuItem"/>s under construction for one context
/// menu. Providers insert, reorder, remove, or fully replace items via this collection; skeleton for
/// <c>FV-008</c>.
/// </summary>
public sealed class FileViewMenuItemCollection
{
    private readonly List<FileViewMenuItem> _items = [];

    /// <summary>Gets the current items in display order.</summary>
    public IReadOnlyList<FileViewMenuItem> Items => _items;

    /// <summary>Inserts <paramref name="item"/> immediately after the item with ID <paramref name="afterId"/>.</summary>
    /// <exception cref="ArgumentException">No item with <paramref name="afterId"/> exists.</exception>
    public void InsertAfter(string afterId, FileViewMenuItem item)
    {
        int index = IndexOf(afterId);
        _items.Insert(index + 1, item);
    }

    /// <summary>Inserts <paramref name="item"/> immediately before the item with ID <paramref name="beforeId"/>.</summary>
    /// <exception cref="ArgumentException">No item with <paramref name="beforeId"/> exists.</exception>
    public void InsertBefore(string beforeId, FileViewMenuItem item)
    {
        int index = IndexOf(beforeId);
        _items.Insert(index, item);
    }

    /// <summary>Removes the item with the given ID, if present.</summary>
    public void Remove(string id)
    {
        int index = _items.FindIndex(existing => string.Equals(existing.Id, id, StringComparison.Ordinal));
        if (index >= 0)
        {
            _items.RemoveAt(index);
        }
    }

    private int IndexOf(string id)
    {
        int index = _items.FindIndex(existing => string.Equals(existing.Id, id, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new ArgumentException($"No menu item with ID '{id}' exists.", nameof(id));
        }

        return index;
    }

    /// <summary>Removes every item, allowing a provider to replace the menu wholesale.</summary>
    public void Clear() => _items.Clear();

    /// <summary>Appends <paramref name="item"/> to the end of the collection.</summary>
    public void Add(FileViewMenuItem item) => _items.Add(item);
}

/// <summary>Context supplied to a <see cref="IFileViewContextMenuProvider"/> while it customizes one menu.</summary>
/// <param name="Scope">Which scope triggered this menu.</param>
/// <param name="Item">The right-clicked item, or <see langword="null"/> for the background scope.</param>
public sealed record FileViewContextMenuContext(FileViewContextMenuScope Scope, FileViewItem? Item);

/// <summary>
/// Customizes the context menu for one scope, without owning any of <see cref="FileView"/>'s markup.
/// Registered via <c>FileView.ContextMenuProviders</c>, evaluated in list order:
/// Background/Directory/File providers first, then every matching <see cref="FileViewContextMenuScope.FileType"/>
/// provider, before the host-level <c>ContextMenuOpening</c> event gets one final look. See
/// <c>doc/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md#context-menu-customization</c> for the
/// worked <c>.zip</c> "UnZip Here…" example this interface exists to support.
/// </summary>
public interface IFileViewContextMenuProvider
{
    /// <summary>Gets the scope this provider applies to.</summary>
    FileViewContextMenuScope Scope { get; }

    /// <summary>
    /// For <see cref="FileViewContextMenuScope.FileType"/>, determines whether this provider applies to
    /// <paramref name="item"/> (e.g. by extension or media type). Ignored for every other scope.
    /// </summary>
    bool Matches(FileViewItem item);

    /// <summary>Mutates the in-progress menu item collection: insert, reorder, remove, or fully replace.</summary>
    void Customize(FileViewContextMenuContext context, FileViewMenuItemCollection items);
}
