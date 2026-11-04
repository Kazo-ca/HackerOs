using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using System.Globalization;
using System.Text.Json;
using Kazo.Os.Apps.FileExplorer.FileControl.Events;
using Kazo.Os.Apps.FileExplorer.FileControl.Icons;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>
/// Code-behind of the desktop mode (ADR 0152 §2-3, ADR 0160 §3): places the items of the folder and the
/// Home and Trash icons on a grid with <see cref="HackDesktopLayout"/>, moves them on a drag, and sends
/// the positions to save to the provider's <see cref="FileViewDesktopBinding"/>.
/// </summary>
/// <remarks>
/// The grid size comes from <c>FileViewDesktop.razor.js</c> (a <c>ResizeObserver</c>): nothing is placed
/// before the first measure. Positions are only written once the folder has been read, so the empty
/// list of a folder still loading never erases what was saved.
/// </remarks>
public partial class FileViewDesktop : IAsyncDisposable
{
    /// <summary>How long the cell asked for a dropped or renamed item waits for that item to appear.</summary>
    private static readonly TimeSpan PendingPlacementLifetime = TimeSpan.FromSeconds(30);

    private readonly Dictionary<FileViewItem, string> _renameBuffers = [];
    private readonly HashSet<string> _systemSelection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (HackDesktopCell Cell, DateTimeOffset At)> _pendingPlacements = new(StringComparer.Ordinal);
    private List<DesktopTile> _tiles = [];
    private HackDesktopArrangement? _arrangement;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ResizeCallback>? _resizeCallback;
    private ElementReference _gridElement;
    private double _width;
    private double _height;
    private int _columns;
    private int _rows;
    private string? _lastSavedSignature;
    private string? _anchorKey;
    private DesktopDrag? _drag;
    private double _containerLeft;
    private double _containerTop;
    private double _marqueeStartX;
    private double _marqueeStartY;
    private double _marqueeCurrentX;
    private double _marqueeCurrentY;
    private bool _isMarqueeActive;
    private bool _marqueeAdditive;
    private bool _disposed;

    /// <summary>The owning <see cref="FileView"/> whose items this renderer displays.</summary>
    [Parameter, EditorRequired]
    public FileView Owner { get; set; } = null!;

    /// <summary>The explorer's binding, cascaded around the view: its <see cref="FileViewMenuBinding.Desktop"/> drives this mode.</summary>
    [CascadingParameter]
    internal FileViewMenuBinding? MenuBinding { get; set; }

    /// <summary>Injected in production; <c>internal</c> so tests can assign a fake, as for <see cref="FileViewIcons"/>.</summary>
    [Inject]
    internal IJSRuntime JavaScript { get; set; } = null!;

    private FileViewDesktopBinding Binding => MenuBinding?.Desktop ?? EmptyBinding;

    private static readonly FileViewDesktopBinding EmptyBinding = new();

    private HackDesktopOptions Options => Binding.Options;

    private string SizeClass => Options.IconSize switch
    {
        HackDesktopIconSize.Small => "size-small",
        HackDesktopIconSize.Large => "size-large",
        _ => "size-medium"
    };

    private int IconPixels => Options.IconSize switch
    {
        HackDesktopIconSize.Small => 32,
        HackDesktopIconSize.Large => 64,
        _ => 48
    };

    /// <summary>The grid tracks, one per cell that fits; items beyond them (a full desktop) add implicit tracks, clipped.</summary>
    private IReadOnlyDictionary<string, object> GridAttributes
    {
        get
        {
            (int width, int height) = HackDesktopLayout.CellSize(Options.IconSize);
            return new Dictionary<string, object>
            {
                ["style"] = FormattableString.Invariant(
                    $"grid-template-columns:repeat({Math.Max(1, _columns)},{width}px);grid-template-rows:repeat({Math.Max(1, _rows)},{height}px);grid-auto-columns:{width}px;grid-auto-rows:{height}px;")
            };
        }
    }

    private double MarqueeLeft => Math.Min(_marqueeStartX, _marqueeCurrentX);
    private double MarqueeTop => Math.Min(_marqueeStartY, _marqueeCurrentY);
    private double MarqueeWidth => Math.Abs(_marqueeCurrentX - _marqueeStartX);
    private double MarqueeHeight => Math.Abs(_marqueeCurrentY - _marqueeStartY);

    private IReadOnlyDictionary<string, object> MarqueeAttributes => new Dictionary<string, object>
    {
        ["style"] = FormattableString.Invariant(
            $"left:{MarqueeLeft}px;top:{MarqueeTop}px;width:{MarqueeWidth}px;height:{MarqueeHeight}px;")
    };

    /// <inheritdoc />
    protected override void OnParametersSet() => Rebuild();

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && JavaScript is not null)
        {
            _module = await JavaScript.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Kazo.Os.Apps.FileExplorer/FileControl/FileViewDesktop.razor.js");
            _resizeCallback = DotNetObjectReference.Create(new ResizeCallback(this));
            await _module.InvokeVoidAsync("observeSize", _gridElement, _resizeCallback);
        }

        await SaveIfChangedAsync();
    }

    /// <summary>Receives the size of the grid (first measure, then each resize of the desktop).</summary>
    internal Task OnResizedAsync(double width, double height)
    {
        if (_disposed || (Math.Abs(width - _width) < 0.5 && Math.Abs(height - _height) < 0.5))
        {
            return Task.CompletedTask;
        }

        _width = width;
        _height = height;
        Rebuild();
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Places every icon: the files of the folder (sorted for the new ones), Home and Trash when shown,
    /// on the saved cells, the cells asked by a drop, or the first free ones.
    /// </summary>
    private void Rebuild()
    {
        if (Owner is null || _width <= 0 || _height <= 0)
        {
            _tiles = [];
            _arrangement = null;
            return;
        }

        HackDesktopOptions options = Options;
        (_columns, _rows) = HackDesktopLayout.GridSize(_width, _height, options.IconSize);
        Dictionary<string, FileViewItem> items = new(StringComparer.Ordinal);
        foreach (FileViewItem item in Owner.Items)
        {
            items.TryAdd(item.FileName, item);
        }

        IReadOnlyList<string> keys = HackDesktopLayout.Order(
            items.Values.Select(static item => new HackDesktopSortItem(
                item.FileName,
                item.IsDirectory,
                item.Metadata is FileMetadata file ? file.Length : 0,
                item.Metadata.Timestamps.ContentModifiedAtUtc)),
            options.Sort,
            options.ShowHome,
            options.ShowTrash);

        _arrangement = HackDesktopLayout.Arrange(
            keys, WithPendingPlacements(keys), _columns, _rows, options.AutoArrange, options.AlignToGrid);

        HashSet<string> shown = new(keys, StringComparer.Ordinal);
        _systemSelection.RemoveWhere(key => !shown.Contains(key));
        List<DesktopTile> tiles = [];
        foreach (string key in keys)
        {
            HackDesktopCell cell = _arrangement.Displayed[key];
            if (items.TryGetValue(key, out FileViewItem? item))
            {
                tiles.Add(new DesktopTile(key, item, item.Label, cell, item.IsDirectory));
            }
            else
            {
                string label = key == HackDesktopItems.Home ? Binding.HomeLabel : Binding.TrashLabel;
                tiles.Add(new DesktopTile(key, null, label, cell, AcceptsDrop: true));
            }
        }

        _tiles = tiles;
    }

    /// <summary>
    /// The saved positions, plus the cells asked for items that just arrived (a drop under the pointer,
    /// a rename that keeps its place). A cell asked for is only taken when it is free, so a new item
    /// never pushes an existing one away.
    /// </summary>
    private IReadOnlyDictionary<string, HackDesktopCell> WithPendingPlacements(IReadOnlyList<string> keys)
    {
        IReadOnlyDictionary<string, HackDesktopCell> saved = Binding.Positions;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string expired in _pendingPlacements.Where(pair => now - pair.Value.At > PendingPlacementLifetime).Select(static pair => pair.Key).ToList())
        {
            _pendingPlacements.Remove(expired);
        }

        if (_pendingPlacements.Count == 0)
        {
            return saved;
        }

        Dictionary<string, HackDesktopCell> merged = new(saved, StringComparer.Ordinal);
        HashSet<string> present = new(keys, StringComparer.Ordinal);
        HashSet<HackDesktopCell> taken = [.. saved.Where(pair => present.Contains(pair.Key) && !_pendingPlacements.ContainsKey(pair.Key)).Select(static pair => pair.Value)];
        foreach ((string key, (HackDesktopCell cell, _)) in _pendingPlacements)
        {
            if (!present.Contains(key))
            {
                continue;
            }

            if (taken.Add(cell))
            {
                merged[key] = cell;
            }
            else
            {
                // The cell is someone else's: the item takes the first free cell, as any new item.
                merged.Remove(key);
            }
        }

        foreach (string arrived in _pendingPlacements.Keys.Where(present.Contains).ToList())
        {
            _pendingPlacements.Remove(arrived);
        }

        return merged;
    }

    /// <summary>
    /// Writes the positions when the arrangement differs from what is saved (a new item, a hand edit,
    /// a move), once per distinct content, and never before the folder has been read.
    /// </summary>
    private async Task SaveIfChangedAsync()
    {
        if (_arrangement is null || !Owner.IsLoaded)
        {
            return;
        }

        bool differs = _arrangement.Changed || Binding.Positions.Keys.Any(key => !_arrangement.Positions.ContainsKey(key));
        if (!differs)
        {
            _lastSavedSignature = null;
            return;
        }

        await SavePositionsAsync(_arrangement.Positions);
    }

    private async Task SavePositionsAsync(IReadOnlyDictionary<string, HackDesktopCell> positions)
    {
        string signature = Signature(positions);
        if (signature == _lastSavedSignature)
        {
            return;
        }

        _lastSavedSignature = signature;
        await Binding.SavePositions(positions);
    }

    private static string Signature(IReadOnlyDictionary<string, HackDesktopCell> positions) =>
        string.Join('\n', positions.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Key + "=" + pair.Value));

    private bool IsSelected(DesktopTile tile) => tile.Item?.IsSelected ?? _systemSelection.Contains(tile.Key);

    private IEnumerable<DesktopTile> SelectedTiles => _tiles.Where(IsSelected);

    /// <summary>Roving tabindex: the first selected icon, or the first icon when nothing is selected.</summary>
    private bool IsTabStop(DesktopTile tile)
    {
        DesktopTile? active = SelectedTiles.FirstOrDefault() ?? _tiles.FirstOrDefault();
        return active is not null && active.Key == tile.Key;
    }

    /// <summary>The menu attributes of the icon, its place on the grid and the keys the script looks up.</summary>
    private IReadOnlyDictionary<string, object> TileAttributes(DesktopTile tile)
    {
        IReadOnlyDictionary<string, object>? menu = tile.Item is { } item ? Owner.MenuAttributesFor(item) : Binding.AttachSystemMenu(tile.Key);
        Dictionary<string, object> attributes = menu is null ? [] : new Dictionary<string, object>(menu);
        attributes["style"] = string.Create(CultureInfo.InvariantCulture, $"grid-column:{tile.Cell.Column + 1};grid-row:{tile.Cell.Row + 1};");
        attributes["data-desktop-key"] = tile.Key;
        if (tile.Item is { } file)
        {
            // FileView's reveal and focus look rows up by this attribute, whatever the mode.
            attributes["data-item-path"] = file.FullPath.Value;
        }

        return attributes;
    }

    private ShellIconDescriptor ResolveIcon(DesktopTile tile)
    {
        if (tile.Item is not { } item)
        {
            return tile.Key == HackDesktopItems.Home
                ? ShellIconDescriptor.Vector(IconLibrary.Lucide, "house")
                : ShellIconDescriptor.Vector(IconLibrary.Lucide, Binding.IsTrashFull ? "trash-2" : "trash");
        }

        if (item.IconOverride is not null)
        {
            return item.IconOverride;
        }

        int lastDot = item.FileName.LastIndexOf('.');
        string? extension = lastDot > 0 ? item.FileName[lastDot..].ToLowerInvariant() : null;
        string? mediaType = item.Metadata is FileMetadata file ? file.MediaType : null;
        return Owner.EffectiveIconProvider.Resolve(new FileViewIconRequest(item.FullPath, item.IsDirectory, extension, mediaType));
    }

    // ---- Selection ----------------------------------------------------------------------------

    private void OnTileClick(DesktopTile tile, MouseEventArgs args)
    {
        if (args.ShiftKey && Owner.AllowMultiSelect && _anchorKey is not null && _arrangement is not null)
        {
            SelectRange(_anchorKey, tile.Key, additive: args.CtrlKey);
            return;
        }

        _anchorKey = tile.Key;
        bool additive = args.CtrlKey && Owner.AllowMultiSelect;
        if (additive && IsSelected(tile))
        {
            Deselect(tile);
            return;
        }

        Select([tile], additive);
    }

    /// <summary>Selects the icons between two, in grid order (down the columns), as Shift+click does.</summary>
    private void SelectRange(string fromKey, string toKey, bool additive)
    {
        List<DesktopTile> ordered = [.. _tiles.OrderBy(tile => (tile.Cell.Column * _rows) + tile.Cell.Row)];
        int from = ordered.FindIndex(tile => tile.Key == fromKey);
        int to = ordered.FindIndex(tile => tile.Key == toKey);
        if (from < 0 || to < 0)
        {
            return;
        }

        Select(ordered.GetRange(Math.Min(from, to), Math.Abs(to - from) + 1), additive);
    }

    /// <summary>Selects icons: the files through the view (its events fire), the system icons here.</summary>
    private void Select(IReadOnlyList<DesktopTile> tiles, bool additive)
    {
        if (!additive)
        {
            _systemSelection.Clear();
        }

        foreach (DesktopTile tile in tiles.Where(static tile => tile.Item is null))
        {
            _systemSelection.Add(tile.Key);
        }

        IEnumerable<FileViewItem> files = tiles.Where(static tile => tile.Item is not null).Select(static tile => tile.Item!);
        IReadOnlyList<FileViewItem> selection = additive ? [.. Owner.SelectedItems.Union(files)] : [.. files];
        Owner.SetSelectionInternal(selection);
        StateHasChanged();
    }

    private void Deselect(DesktopTile tile)
    {
        if (tile.Item is { } item)
        {
            item.Deselect();
        }
        else
        {
            _systemSelection.Remove(tile.Key);
            StateHasChanged();
        }
    }

    private void ClearSelection()
    {
        _systemSelection.Clear();
        if (Owner.SelectedItems.Count > 0)
        {
            Owner.ClearSelect();
        }

        StateHasChanged();
    }

    /// <summary>A right click: an icon outside the selection becomes the selection, then the OS menu opens.</summary>
    private void OnTileContextMenu(DesktopTile tile)
    {
        if (!IsSelected(tile))
        {
            _anchorKey = tile.Key;
            Select([tile], additive: false);
        }
    }

    // ---- Lasso ----------------------------------------------------------------------------------

    private async Task OnGridMouseDownAsync(MouseEventArgs args)
    {
        if (args.Button != 0 || _module is null)
        {
            return;
        }

        if (!args.CtrlKey)
        {
            ClearSelection();
        }

        if (!Owner.AllowMultiSelect)
        {
            return;
        }

        ContainerRect origin = await _module.InvokeAsync<ContainerRect>("getOrigin", _gridElement);
        _containerLeft = origin.Left;
        _containerTop = origin.Top;
        _marqueeStartX = _marqueeCurrentX = args.ClientX - _containerLeft;
        _marqueeStartY = _marqueeCurrentY = args.ClientY - _containerTop;
        _marqueeAdditive = args.CtrlKey;
        _isMarqueeActive = true;
    }

    private void OnGridMouseMove(MouseEventArgs args)
    {
        if (!_isMarqueeActive)
        {
            return;
        }

        _marqueeCurrentX = args.ClientX - _containerLeft;
        _marqueeCurrentY = args.ClientY - _containerTop;
        StateHasChanged();
    }

    private async Task OnGridMouseUpAsync(MouseEventArgs args)
    {
        if (!_isMarqueeActive)
        {
            return;
        }

        _isMarqueeActive = false;
        if ((MarqueeWidth < 2 && MarqueeHeight < 2) || _module is null)
        {
            StateHasChanged();
            return;
        }

        string[] keys = await _module.InvokeAsync<string[]>(
            "getIntersectingKeys", _gridElement, MarqueeLeft, MarqueeTop, MarqueeLeft + MarqueeWidth, MarqueeTop + MarqueeHeight);
        HashSet<string> matched = new(keys, StringComparer.Ordinal);
        Select([.. _tiles.Where(tile => matched.Contains(tile.Key))], _marqueeAdditive);
    }

    // ---- Keyboard ---------------------------------------------------------------------------------

    private Task OnTileKeyDownAsync(DesktopTile tile, KeyboardEventArgs args) => args.Key switch
    {
        "Enter" => OpenAsync(tile),
        "F2" when tile.Item is { } item => RenameAsync(item),
        "Delete" when tile.Item is not null => DeleteAsync(tile, args.ShiftKey),
        "ArrowDown" => MoveFocusAsync(tile, 0, 1, args),
        "ArrowUp" => MoveFocusAsync(tile, 0, -1, args),
        "ArrowRight" => MoveFocusAsync(tile, 1, 0, args),
        "ArrowLeft" => MoveFocusAsync(tile, -1, 0, args),
        _ => Task.CompletedTask
    };

    /// <summary>
    /// Arrow keys move on the grid itself (ADR 0160 §3): to the nearest icon in that direction, the one
    /// on the same row or column first. Shift extends the selection from the anchor.
    /// </summary>
    private async Task MoveFocusAsync(DesktopTile from, int dx, int dy, KeyboardEventArgs args)
    {
        DesktopTile? next = _tiles
            .Where(tile => tile.Key != from.Key)
            .Select(tile => (tile, along: ((tile.Cell.Column - from.Cell.Column) * dx) + ((tile.Cell.Row - from.Cell.Row) * dy),
                across: Math.Abs(dx != 0 ? tile.Cell.Row - from.Cell.Row : tile.Cell.Column - from.Cell.Column)))
            .Where(static entry => entry.along > 0)
            .OrderBy(static entry => entry.across)
            .ThenBy(static entry => entry.along)
            .Select(static entry => entry.tile)
            .FirstOrDefault();
        if (next is null)
        {
            return;
        }

        if (args.ShiftKey && Owner.AllowMultiSelect)
        {
            _anchorKey ??= from.Key;
            SelectRange(_anchorKey, next.Key, additive: false);
        }
        else
        {
            _anchorKey = next.Key;
            Select([next], additive: false);
        }

        if (_module is not null)
        {
            await _module.InvokeAsync<bool>("focusKey", _gridElement, next.Key);
        }
    }

    // ---- Open, rename, delete -------------------------------------------------------------------

    private Task OpenAsync(DesktopTile tile) =>
        tile.Item is { } item ? Owner.ActivateItemAsync(item) : Binding.OpenSystemItem(tile.Key);

    private Task RenameAsync(FileViewItem item)
    {
        item.Select();
        item.Rename();
        return Task.CompletedTask;
    }

    private Task DeleteAsync(DesktopTile tile, bool permanent)
    {
        if (!IsSelected(tile))
        {
            Select([tile], additive: false);
        }

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

    private Task OnRenameKeyDownAsync(FileViewItem item, KeyboardEventArgs args)
    {
        switch (args.Key)
        {
            case "Enter":
                return CommitRenameAsync(item);
            case "Escape":
                _renameBuffers.Remove(item);
                Owner.CancelRename(item);
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>Renames in place; the new name keeps the old one's cell (a rename is not a new item).</summary>
    private Task CommitRenameAsync(FileViewItem item)
    {
        if (!_renameBuffers.Remove(item, out string? value))
        {
            return Task.CompletedTask;
        }

        string proposed = value.Trim();
        if (_arrangement?.Displayed.TryGetValue(item.FileName, out HackDesktopCell cell) == true
            && proposed.Length > 0
            && proposed != item.FileName)
        {
            _pendingPlacements[proposed] = (cell, DateTimeOffset.UtcNow);
        }

        return Owner.CommitRenameAsync(item, value);
    }

    // ---- Drag and drop ----------------------------------------------------------------------------

    /// <summary>
    /// Starts a drag of the selected icons. The files go on the drag as for any view (another window
    /// may take them); the icon keys mark the drag as this desktop's own, for a move on the grid.
    /// </summary>
    private Task OnTileDragStartAsync(DesktopTile tile, DragEventArgs args)
    {
        if (!Owner.AllowDragDrop)
        {
            return Task.CompletedTask;
        }

        if (!IsSelected(tile))
        {
            _anchorKey = tile.Key;
            Select([tile], additive: false);
        }

        string[] keys = [.. SelectedTiles.Select(static selected => selected.Key)];
        _drag = new DesktopDrag(keys, tile.Key);
        IReadOnlyList<FileViewItem> files = [.. SelectedTiles.Where(static selected => selected.Item is not null).Select(static selected => selected.Item!)];
        return Owner.StartDesktopDragAsync(files, JsonSerializer.Serialize(keys, FileViewDesktopJsonContext.Default.StringArray));
    }

    private void OnDragEnd() => _drag = null;

    /// <summary>
    /// A drop on the grid: the desktop's own icons move to the cell under the pointer; anything else
    /// (files of another window, host files, an app from the Start menu) is copied or moved into the
    /// folder, and the new icons are placed under the pointer.
    /// </summary>
    private async Task OnGridDropAsync(DragEventArgs args)
    {
        if (!Owner.AllowDragDrop || _module is null || _arrangement is null)
        {
            return;
        }

        ContainerRect origin = await _module.InvokeAsync<ContainerRect>("getOrigin", _gridElement);
        HackDesktopCell target = HackDesktopLayout.CellAtPoint(
            args.ClientX - origin.Left, args.ClientY - origin.Top, Options.IconSize, _columns, _rows);

        if (_drag is { } drag)
        {
            _drag = null;
            IReadOnlyDictionary<string, HackDesktopCell> positions = HackDesktopLayout.Move(
                _arrangement, drag.Keys, drag.Anchor, target, _columns, _rows, Options.AutoArrange);
            // Shown at once; the saved copy comes back through the host's parameters.
            _arrangement = _arrangement with { Displayed = Merge(_arrangement.Displayed, positions) };
            _tiles = [.. _tiles.Select(tile => _arrangement.Displayed.TryGetValue(tile.Key, out HackDesktopCell cell) ? tile with { Cell = cell } : tile)];
            StateHasChanged();
            await SavePositionsAsync(positions);
            return;
        }

        FileViewDropRequestedEventArgs? drop = await Owner.ReadDropAsync(Owner.CurrentDirectory, args);
        if (drop is null)
        {
            return;
        }

        IEnumerable<string> names = drop.Entries.Select(static entry => HackFileOperations.GetName(entry.Path.Value))
            .Concat(drop.HostFiles.Select(static file => file.Name));
        int offset = 0;
        foreach (string name in names.Distinct(StringComparer.Ordinal))
        {
            // The dropped items go down the column from the pointer, as a group.
            _pendingPlacements[name] = (new HackDesktopCell(target.Column + ((target.Row + offset) / _rows), (target.Row + offset) % _rows), DateTimeOffset.UtcNow);
            offset++;
        }

        await Owner.CompleteDropAsync(Owner.CurrentDirectory, drop);
    }

    /// <summary>A drop on an icon: into a folder, into the home folder, or to the trash.</summary>
    private async Task OnTileDropAsync(DesktopTile tile, DragEventArgs args)
    {
        if (!tile.AcceptsDrop || !Owner.AllowDragDrop)
        {
            return;
        }

        _drag = null;
        if (tile.Item is { } folder)
        {
            await Owner.OnItemDropAsync(folder, args);
            return;
        }

        FileViewDropRequestedEventArgs? drop = await Owner.ReadDropAsync(null, args);
        if (drop is null)
        {
            return;
        }

        try
        {
            await Binding.DropOnSystemItem(tile.Key, drop);
        }
        finally
        {
            await Owner.ReleaseHostFilesAsync(drop);
        }
    }

    private static Dictionary<string, HackDesktopCell> Merge(IReadOnlyDictionary<string, HackDesktopCell> displayed, IReadOnlyDictionary<string, HackDesktopCell> moved)
    {
        Dictionary<string, HackDesktopCell> merged = new(displayed, StringComparer.Ordinal);
        foreach ((string key, HackDesktopCell cell) in moved)
        {
            if (merged.ContainsKey(key))
            {
                merged[key] = cell;
            }
        }

        return merged;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("unobserveSize", _gridElement);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The page is going away: nothing to detach.
            }
        }

        _resizeCallback?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>One icon on the grid: a file of the folder, or a system icon (<see cref="Item"/> is <see langword="null"/>).</summary>
    private sealed record DesktopTile(string Key, FileViewItem? Item, string Label, HackDesktopCell Cell, bool AcceptsDrop);

    /// <summary>The icons dragged on their own desktop, and the one under the pointer.</summary>
    private sealed record DesktopDrag(IReadOnlyList<string> Keys, string Anchor);

    /// <summary>
    /// The target of the script's <c>ResizeObserver</c>: a small object rather than the component, so the
    /// component has no public JS-invokable method.
    /// </summary>
    internal sealed class ResizeCallback(FileViewDesktop owner)
    {
        /// <summary>Called by <c>FileViewDesktop.razor.js</c> with the grid's size in CSS pixels.</summary>
        [JSInvokable]
        public Task OnDesktopResized(double width, double height) =>
            owner.InvokeAsync(() => owner.OnResizedAsync(width, height));
    }
}
