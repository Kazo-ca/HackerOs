using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>Code-behind for the sortable Details-mode renderer (<c>FV-002</c>).</summary>
public partial class FileViewDetails
{
    private const string NameColumnKey = "name";

    /// <summary>
    /// The default columns, their headers and the Type values in the owner's language (ADR 0104);
    /// keys stay stable for sorting and host persistence.
    /// </summary>
    private IReadOnlyList<FileViewColumn> DefaultColumns
    {
        get
        {
            Microsoft.Extensions.Localization.IStringLocalizer<Strings> text = Owner.L;
            return
            [
                new(NameColumnKey, text["Column.Name"], static item => item.FileName),
                new("type", text["Column.Type"], item => FormatType(item, text)),
                new("size", text["Column.Size"], static item => item.Metadata is FileMetadata file ? file.Length : -1L),
                new("modified", text["Column.Modified"], static item => item.Metadata.Timestamps.ContentModifiedAtUtc)
            ];
        }
    }

    /// <summary>Formats one entry's Type column value: <c>"Folder"</c>, <c>".ext File"</c>, or plain <c>"File"</c> when it has no extension.</summary>
    internal static string FormatType(FileViewItem item, Microsoft.Extensions.Localization.IStringLocalizer<Strings> text)
    {
        if (item.IsDirectory)
        {
            return text["Type.Folder"];
        }

        string? extension = GetExtension(item.FileName);
        return extension is null ? text["Type.File"] : text["Type.Extension", extension];
    }

    private readonly Dictionary<FileViewItem, string> _renameBuffers = [];
    private string _sortColumnKey = NameColumnKey;
    private bool _sortDescending;

    /// <summary>The owning <see cref="FileView"/> whose items this renderer displays.</summary>
    [Parameter, EditorRequired]
    public FileView Owner { get; set; } = null!;

    private IReadOnlyList<FileViewColumn> EffectiveColumns => Owner.Columns ?? DefaultColumns;

    internal IEnumerable<FileViewItem> SortedItems
    {
        get
        {
            FileViewColumn column = EffectiveColumns.FirstOrDefault(c => c.Key == _sortColumnKey) ?? EffectiveColumns[0];

            // Folders always group before files, independent of sort column/direction — the
            // selected sort only orders within each group, matching common file-explorer UX.
            IOrderedEnumerable<FileViewItem> byGroup = Owner.Items.OrderBy(static item => item.IsDirectory ? 0 : 1);
            return _sortDescending
                ? byGroup.ThenByDescending(column.SortAccessor)
                : byGroup.ThenBy(column.SortAccessor);
        }
    }

    internal void ToggleSort(string key)
    {
        if (_sortColumnKey == key)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumnKey = key;
            _sortDescending = false;
        }
    }

    private string AriaSort(string key) =>
        key != _sortColumnKey ? "none" : _sortDescending ? "descending" : "ascending";

    private static readonly IReadOnlyDictionary<string, object> EmptyAttributes =
        new Dictionary<string, object>();

    private static IReadOnlyDictionary<string, object> GetHeaderAttributes(FileViewColumn column) =>
        column.Width is { } width
            ? new Dictionary<string, object> { ["style"] = FormattableString.Invariant($"width:{width}px") }
            : EmptyAttributes;

    /// <summary>
    /// Roving tabindex (<c>FV-011</c>): only the active item — the selection, or the first row when
    /// nothing is selected yet — is ever a Tab stop, so Tab moves past the whole table in one step
    /// instead of stopping at every row. Arrow keys move the active item within the table.
    /// </summary>
    internal bool IsTabStop(FileViewItem item) =>
        item.IsSelected || (Owner.SelectedItem is null && ReferenceEquals(item, SortedItems.FirstOrDefault()));

    internal async Task OnRowClickAsync(FileViewItem item, MouseEventArgs args)
    {
        await CommitPendingRenameElsewhereAsync(item);
        await Owner.ClickItemAsync(item, args.CtrlKey);
    }

    internal async Task OnRowDoubleClickAsync(FileViewItem item)
    {
        await CommitPendingRenameElsewhereAsync(item);
        await Owner.DoubleClickItemAsync(item);
    }

    private async Task OnRowContextMenuAsync(FileViewItem item, MouseEventArgs args)
    {
        await CommitPendingRenameElsewhereAsync(item);
        await Owner.OnItemContextMenuAsync(args, item);
    }

    /// <summary>
    /// Interacting with a different row while one item is mid-rename should accept that rename, not strand
    /// its textbox open — the blur that would normally do this can lose the race against the click's own
    /// re-render, so the click path commits explicitly instead of relying on it.
    /// </summary>
    private Task CommitPendingRenameElsewhereAsync(FileViewItem clicked)
    {
        FileViewItem? renaming = Owner.Items.FirstOrDefault(i => i.IsRenaming && !ReferenceEquals(i, clicked));
        return renaming is null ? Task.CompletedTask : CommitRenameAsync(renaming);
    }

    internal Task OnRowKeyDownAsync(FileViewItem item, KeyboardEventArgs args) => args.Key switch
    {
        "Enter" => Owner.ActivateItemAsync(item),
        "F2" => RenameAsync(item),
        "Delete" => DeleteAsync(item, args.ShiftKey),
        "ArrowDown" => MoveActiveAsync(item, 1),
        "ArrowUp" => MoveActiveAsync(item, -1),
        _ => Task.CompletedTask
    };

    private Task MoveActiveAsync(FileViewItem item, int direction)
    {
        List<FileViewItem> ordered = [.. SortedItems];
        int index = ordered.IndexOf(item) + direction;
        return index >= 0 && index < ordered.Count ? Owner.MoveActiveItemAsync(ordered[index]) : Task.CompletedTask;
    }

    private Task OnRowDragStartAsync(FileViewItem item, DragEventArgs args) => Owner.OnItemDragStartAsync(item, args);

    private Task OnRowDropAsync(FileViewItem item, DragEventArgs args) => Owner.OnItemDropAsync(item, args);

    private Task RenameAsync(FileViewItem item)
    {
        item.Select();
        item.Rename();
        return Task.CompletedTask;
    }

    private Task DeleteAsync(FileViewItem item, bool permanent)
    {
        item.Select();
        return Owner.DeleteSelectedAsync(permanent);
    }

    private string GetRenameBuffer(FileViewItem item)
    {
        if (!_renameBuffers.TryGetValue(item, out string? value))
        {
            value = item.FileName;
            _renameBuffers[item] = value;
        }

        return value;
    }

    private void SetRenameBuffer(FileViewItem item, string value) => _renameBuffers[item] = value;

    private Task OnRenameKeyDownAsync(FileViewItem item, KeyboardEventArgs args) => args.Key switch
    {
        "Enter" => CommitRenameAsync(item),
        "Escape" => CancelRenameAsync(item),
        _ => Task.CompletedTask
    };

    private Task CommitRenameAsync(FileViewItem item)
    {
        string value = GetRenameBuffer(item);
        _renameBuffers.Remove(item);
        return Owner.CommitRenameAsync(item, value);
    }

    private Task CancelRenameAsync(FileViewItem item)
    {
        _renameBuffers.Remove(item);
        Owner.CancelRename(item);
        return Task.CompletedTask;
    }

    private ShellIconDescriptor ResolveIcon(FileViewItem item)
    {
        if (item.IconOverride is not null)
        {
            return item.IconOverride;
        }

        string? extension = GetExtension(item.FileName);
        string? mediaType = item.Metadata is FileMetadata file ? file.MediaType : null;
        return Owner.EffectiveIconProvider.Resolve(new FileViewIconRequest(item.FullPath, item.IsDirectory, extension, mediaType));
    }

    private static string? GetExtension(string fileName)
    {
        int lastDot = fileName.LastIndexOf('.');
        return lastDot > 0 ? fileName[lastDot..].ToLowerInvariant() : null;
    }

    private string FormatValue(FileViewColumn column, FileViewItem item)
    {
        IComparable value = column.SortAccessor(item);
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToString("yyyy-MM-dd HH:mm"),
            long size and < 0 => string.Empty,
            long size => FormatBytes(size, Owner.L),
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <summary>Formats a size with a binary unit, in the owner's language (<c>1,5 Ko</c> in French).</summary>
    internal static string FormatBytes(long bytes, Microsoft.Extensions.Localization.IStringLocalizer<Strings> text)
    {
        string[] units = ["Size.Bytes", "Size.Kilobytes", "Size.Megabytes", "Size.Gigabytes"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return text[units[unitIndex], size];
    }
}
