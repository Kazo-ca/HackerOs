using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Icons;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Code-behind for the tile-grid Icons-mode renderer (<c>FV-003</c>), including marquee multi-select.
/// Rename handling mirrors <see cref="FileViewDetails"/> exactly; only the grid/marquee logic is new here.
/// </summary>
public partial class FileViewIcons : IAsyncDisposable
{
    private readonly Dictionary<FileViewItem, string> _renameBuffers = [];
    private IJSObjectReference? _module;
    private ElementReference _gridElement;
    private double _containerLeft;
    private double _containerTop;
    private double _marqueeStartX;
    private double _marqueeStartY;
    private double _marqueeCurrentX;
    private double _marqueeCurrentY;
    private bool _isMarqueeActive;
    private bool _marqueeAdditive;

    /// <summary>The owning <see cref="FileView"/> whose items this renderer displays.</summary>
    [Parameter, EditorRequired]
    public FileView Owner { get; set; } = null!;

    /// <summary>
    /// Injected in production; <c>internal</c> (rather than <c>private</c>) so tests can assign a fake
    /// directly, the same way they set <see cref="Owner"/> without going through Blazor's parameter
    /// pipeline — this component is never instantiated via DI in this solution's test harness (no bUnit).
    /// </summary>
    [Inject]
    internal IJSRuntime JavaScript { get; set; } = null!;

    private double MarqueeLeft => Math.Min(_marqueeStartX, _marqueeCurrentX);
    private double MarqueeTop => Math.Min(_marqueeStartY, _marqueeCurrentY);
    private double MarqueeWidth => Math.Abs(_marqueeCurrentX - _marqueeStartX);
    private double MarqueeHeight => Math.Abs(_marqueeCurrentY - _marqueeStartY);

    private IReadOnlyDictionary<string, object> GetMarqueeAttributes() => new Dictionary<string, object>
    {
        ["style"] = FormattableString.Invariant(
            $"left:{MarqueeLeft}px;top:{MarqueeTop}px;width:{MarqueeWidth}px;height:{MarqueeHeight}px;")
    };

    internal async Task OnGridMouseDownAsync(MouseEventArgs args)
    {
        if (!Owner.AllowMultiSelect || args.Button != 0)
        {
            return;
        }

        _module ??= await JavaScript.InvokeAsync<IJSObjectReference>(
            "import", "./_content/HackerOs.Apps.FileExplorer/FileControl/FileViewIcons.razor.js");
        ContainerRect rect = await _module.InvokeAsync<ContainerRect>("getContainerRect", _gridElement);
        _containerLeft = rect.Left;
        _containerTop = rect.Top;
        _marqueeStartX = _marqueeCurrentX = args.ClientX - _containerLeft;
        _marqueeStartY = _marqueeCurrentY = args.ClientY - _containerTop;
        _marqueeAdditive = args.CtrlKey;
        _isMarqueeActive = true;
    }

    internal void OnGridMouseMove(MouseEventArgs args)
    {
        if (!_isMarqueeActive)
        {
            return;
        }

        _marqueeCurrentX = args.ClientX - _containerLeft;
        _marqueeCurrentY = args.ClientY - _containerTop;
        StateHasChanged();
    }

    internal async Task OnGridMouseUpAsync(MouseEventArgs args)
    {
        if (!_isMarqueeActive)
        {
            return;
        }

        _isMarqueeActive = false;
        if (MarqueeWidth < 2 && MarqueeHeight < 2)
        {
            return;
        }

        double left = MarqueeLeft;
        double top = MarqueeTop;
        double right = left + MarqueeWidth;
        double bottom = top + MarqueeHeight;
        string[] paths = await _module!.InvokeAsync<string[]>(
            "getIntersectingPaths", _gridElement, left, top, right, bottom);
        HashSet<string> matched = new(paths, StringComparer.Ordinal);
        IReadOnlyList<FileViewItem> intersecting = [.. Owner.Items.Where(item => matched.Contains(item.FullPath.Value))];
        IReadOnlyList<FileViewItem> selected = _marqueeAdditive
            ? [.. Owner.SelectedItems.Union(intersecting)]
            : intersecting;
        Owner.SetSelectionInternal(selected);
    }

    /// <summary>
    /// Roving tabindex (<c>FV-011</c>): only the active item — the selection, or the first tile when
    /// nothing is selected yet — is ever a Tab stop, so Tab moves past the whole grid in one step instead
    /// of stopping at every tile. Arrow keys move the active item within the grid.
    /// </summary>
    internal bool IsTabStop(FileViewItem item) =>
        item.IsSelected || (Owner.SelectedItem is null && ReferenceEquals(item, Owner.Items.FirstOrDefault()));

    internal Task OnTileClick(FileViewItem item, MouseEventArgs args) => Owner.ClickItemAsync(item, args.CtrlKey);

    internal Task OnTileDoubleClickAsync(FileViewItem item) => Owner.DoubleClickItemAsync(item);

    internal Task OnTileContextMenuAsync(FileViewItem item, MouseEventArgs args) => Owner.OnItemContextMenuAsync(args, item);

    internal Task OnTileKeyDownAsync(FileViewItem item, KeyboardEventArgs args) => args.Key switch
    {
        "Enter" => Owner.ActivateItemAsync(item),
        "F2" => RenameAsync(item),
        "Delete" => DeleteAsync(item, args.ShiftKey),
        // The grid visually wraps, but true 2D layout (knowing tiles-per-row) isn't available from C# —
        // all four arrows move through Owner.Items order, same as a flat listbox. A scoping decision, not
        // a bug: still fully keyboard-operable, just not spatially "up a row"/"down a row" like a native grid.
        "ArrowDown" or "ArrowRight" => MoveActiveAsync(item, 1),
        "ArrowUp" or "ArrowLeft" => MoveActiveAsync(item, -1),
        _ => Task.CompletedTask
    };

    private Task MoveActiveAsync(FileViewItem item, int direction)
    {
        List<FileViewItem> ordered = [.. Owner.Items];
        int index = ordered.IndexOf(item) + direction;
        return index >= 0 && index < ordered.Count ? Owner.MoveActiveItemAsync(ordered[index]) : Task.CompletedTask;
    }

    internal Task OnTileDragStartAsync(FileViewItem item, DragEventArgs args) => Owner.OnItemDragStartAsync(item, args);

    internal Task OnTileDropAsync(FileViewItem item, DragEventArgs args) => Owner.OnItemDropAsync(item, args);

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

    /// <summary>The icon of a folder whose preview shows entries (ADR 0185 §14).</summary>
    private static readonly ShellIconDescriptor OpenFolderIcon = ShellIconDescriptor.Vector(IconLibrary.Lucide, "folder-open");

    /// <summary>Whether a tile gets a preview: a folder drawn with the provider's icon (not an override).</summary>
    internal static bool WantsPreview(FileViewItem item) => item.IsDirectory && item.IconOverride is null;

    /// <summary>The preview of a folder's tile, <see langword="null"/> when none was read yet or the tile gets none.</summary>
    internal IReadOnlyList<ShellIconDescriptor>? PreviewFor(FileViewItem item) => WantsPreview(item) ? Owner.GetFolderPreview(item) : null;

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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }
    }
}

/// <summary>JS-interop return shape for <c>FileViewIcons.razor.js</c>'s <c>getContainerRect</c>.</summary>
internal sealed class ContainerRect
{
    public double Left { get; set; }
    public double Top { get; set; }
}
