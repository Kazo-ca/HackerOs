using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.DragDrop;
using HackerOs.Apps.FileExplorer.FileControl.ContextMenu;
using HackerOs.Apps.FileExplorer.FileControl.Events;
using HackerOs.Apps.FileExplorer.FileControl.Icons;
using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace HackerOs.Apps.FileExplorer.FileControl;

/// <summary>
/// Shared backing for <see cref="FileView"/> — navigation, selection, inline rename, folder activation,
/// live-watch wiring, and control-initiated create/delete. Markup lives in <c>FileView.razor</c>; view-mode
/// renderers (<c>FileViewDetails</c>, ...) call back into the <c>internal</c> members here. See
/// <c>doc/en/wasm/dev/old/Global-FileView-And-MessagingSystem/FileViewControl.md</c>.
/// </summary>
public partial class FileView : IAsyncDisposable
{
    /// <summary>The custom <c>DataTransfer</c> MIME type <c>FileView.razor.js</c> reads/writes drag payloads under.</summary>
    internal const string DragMimeType = "application/vnd.hackeros.file-drag+json";

    /// <summary>
    /// The <c>DataTransfer</c> type of an app dragged from the Start menu (ADR 0160 §6), <c>{"appId":"…"}</c>:
    /// dropped on a folder, it becomes a <c>.desktop</c> shortcut there.
    /// </summary>
    internal const string AppDragMimeType = "application/vnd.hackeros.app+json";

    /// <summary>The MIME type of a drag of desktop icons on their own desktop (ADR 0160 §3): their position keys.</summary>
    internal const string DesktopItemMimeType = "application/vnd.hackeros.desktop-item+json";

    private readonly List<FileViewItem> _items = [];
    private readonly HashSet<FileViewItem> _selection = [];
    private bool _initialized;
    private long _currentDirectoryRevision;
    private ITopicChannelSubscription<FileSystemChangeEvent>? _watchSubscription;
    private CancellationTokenSource? _watchLoopCts;
    private FileViewContextMenu? _contextMenu;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<FileView>? _pasteCallbacks;
    private bool _pasteTargetAttached;
    private ElementReference _rootElement;

    /// <summary>Item to scroll into view and focus after the next render (<see cref="RevealAsync"/>).</summary>
    private string? _pendingReveal;
    private FileSystemDirectorySnapshot? _lastSnapshot;

    /// <summary>Scoped filesystem access supplied by the host app. Required.</summary>
    [Parameter, EditorRequired]
    public IAppFileSystemGateway FileSystem { get; set; } = null!;

    /// <summary>
    /// Optional scoped directory-watch access. When supplied, live change notifications replace/augment
    /// manual <see cref="RefreshAsync"/> calls.
    /// </summary>
    [Parameter]
    public IAppFileSystemWatchGateway? Watch { get; set; }

    // The app this view belongs to is HackComponentBase.AppContext, cascaded by the window host: its
    // process's language is the language of the view's texts (ADR 0104), and the view's handlers run
    // in its frame (ADR 0161 §1). Without one, the ambient app's language applies.

    /// <summary>Gets the library's texts in the owning app's language, shared by the mode renderers.</summary>
    internal Microsoft.Extensions.Localization.IStringLocalizer<Strings> L => AppContext is { } context
        ? HackerOs.AppSdk.Globalization.HackLocalization.GetLocalizer<Strings>(context)
        : HackerOs.AppSdk.Globalization.HackLocalization.GetLocalizer<Strings>();

    /// <summary>Optional scoped intent access, required only when <see cref="FolderActivation"/> is <see cref="FileViewFolderActivationMode.NewWindow"/>.</summary>
    [Parameter]
    public IAppIntentGateway? Intents { get; set; }

    /// <summary>The directory shown on first render.</summary>
    [Parameter, EditorRequired]
    public VirtualPath InitialDirectory { get; set; }

    /// <summary>The active view mode. Two-way bindable.</summary>
    [Parameter]
    public FileViewMode Mode { get; set; } = FileViewMode.Details;

    /// <summary>Callback invoked when <see cref="Mode"/> changes via <c>@bind-Mode</c>.</summary>
    [Parameter]
    public EventCallback<FileViewMode> ModeChanged { get; set; }

    /// <summary>Controls what a directory item's activation (double-click/Enter) does.</summary>
    [Parameter]
    public FileViewFolderActivationMode FolderActivation { get; set; } = FileViewFolderActivationMode.Navigate;

    /// <summary>Required when <see cref="FolderActivation"/> is <see cref="FileViewFolderActivationMode.Custom"/>.</summary>
    [Parameter]
    public Func<FileViewItem, Task>? OnCustomFolderActivate { get; set; }

    /// <summary>Overrides the default <see cref="Icons.IShellIconProvider"/> for this instance only.</summary>
    [Parameter]
    public IShellIconProvider? IconProvider { get; set; }

    /// <summary>
    /// The icons drawn when <see cref="IconProvider"/> is not supplied. The control loads with the
    /// File Explorer, after the OS has built its services, so it keeps its own instead of asking the
    /// host for one (ADR 0161 §1).
    /// </summary>
    private static readonly IShellIconProvider DefaultIconProvider = new DefaultShellIconProvider();

    /// <summary>
    /// Injected in production; <c>internal</c> (rather than <c>private</c>) so tests can assign a fake
    /// directly, the same way they set <see cref="FileSystem"/> without going through Blazor's parameter
    /// pipeline — this component is never instantiated via DI in this solution's test harness (no bUnit).
    /// </summary>
    [Inject]
    internal IJSRuntime JavaScript { get; set; } = null!;

    /// <summary>Ordered context-menu customizers; see <see cref="IFileViewContextMenuProvider"/>.</summary>
    [Parameter]
    public IReadOnlyList<IFileViewContextMenuProvider> ContextMenuProviders { get; set; } = [];

    /// <summary>Whether more than one item may be selected at once. Defaults to <see langword="true"/>.</summary>
    [Parameter]
    public bool AllowMultiSelect { get; set; } = true;

    /// <summary>Whether drag &amp; drop is enabled. Defaults to <see langword="true"/>.</summary>
    [Parameter]
    public bool AllowDragDrop { get; set; } = true;

    /// <summary>
    /// This instance's app id, carried as <c>SourceAppId</c> on outgoing drag payloads (e.g.
    /// <c>context.Manifest.Id</c>). Purely informational — a drop never needs to know or trust the
    /// source app, so an unset value does not disable drag-drop.
    /// </summary>
    [Parameter]
    public string? OwnerAppId { get; set; }

    /// <summary>Overrides the default Details-mode column set.</summary>
    [Parameter]
    public IReadOnlyList<FileViewColumn>? Columns { get; set; }

    /// <summary>
    /// Optional predicate limiting which entries appear in <see cref="Items"/>, across all three modes —
    /// e.g. a host's search box. Evaluated fresh whenever <see cref="Items"/> is (re)built; call
    /// <see cref="RefreshFilter"/> after changing what this delegate would return to re-apply it against
    /// the most recently fetched listing without a filesystem round-trip. A filtered-out item behaves as
    /// though it doesn't exist: not rendered, not selectable, not counted.
    /// </summary>
    [Parameter]
    public Func<FileViewItem, bool>? Filter { get; set; }

    /// <summary>
    /// When supplied, the control's own delete actions (Delete key, Shift+Delete, the "Delete" and
    /// "Delete permanently" menu entries) are handed to the host instead of deleting through
    /// <see cref="FileSystem"/>; the host decides between the trash and a permanent deletion and
    /// refreshes the view. Without it, "Delete" removes the items permanently and the
    /// "Delete permanently" entry is not offered (there would be no difference).
    /// </summary>
    [Parameter]
    public EventCallback<FileViewDeleteRequestedEventArgs> DeleteRequested { get; set; }

    /// <summary>
    /// When supplied, every drop (on a folder, the background, a tree node, a <see cref="FileViewBreadcrumb"/>
    /// segment or a <see cref="FileViewDropZone"/>), including files dragged from the host system, is
    /// handed to the host, which runs the transfer with its conflict rules (ADR 0058, section 1).
    /// Without it, a HackerOS drag onto a folder or the background is moved (or copied with Ctrl) by
    /// the control itself, and host files are ignored.
    /// </summary>
    [Parameter]
    public EventCallback<FileViewDropRequestedEventArgs> DropRequested { get; set; }

    /// <summary>
    /// When supplied, Ctrl+X, Ctrl+C, Ctrl+V and the "Cut", "Copy", "Paste" and "Paste Into Folder"
    /// menu entries are handed to the host, which owns the clipboard. Without it, none of them is offered.
    /// </summary>
    [Parameter]
    public EventCallback<FileViewClipboardRequestedEventArgs> ClipboardRequested { get; set; }

    /// <summary>Whether the clipboard holds files: "Paste" entries are greyed out otherwise. Set by the host.</summary>
    [Parameter]
    public bool CanPaste { get; set; }

    /// <summary>
    /// Paths currently cut to the clipboard: their items are shown dimmed until the paste (a cut
    /// removes nothing by itself). Set by the host from <c>HackClipboard</c>.
    /// </summary>
    [Parameter]
    public IReadOnlyCollection<string>? CutPaths { get; set; }

    /// <summary>
    /// Gets or sets the binding to the OS context menu, cascaded by the File Explorer (ADR 0155 §1);
    /// <see langword="null"/> keeps the view's own menu.
    /// </summary>
    [CascadingParameter]
    internal FileViewMenuBinding? MenuBinding { get; set; }

    /// <summary>Gets whether the view opens its own menu (no OS menu binding).</summary>
    internal bool UsesOwnMenu => MenuBinding is null;

    /// <summary>Gets the attributes that open the OS menu on <paramref name="item"/> (the background for <see langword="null"/>).</summary>
    internal IReadOnlyDictionary<string, object>? MenuAttributesFor(FileViewItem? item) => MenuBinding?.Attach(item);

    /// <summary>Gets whether items may be renamed in place (always, unless the OS menu binding forbids it).</summary>
    internal bool CanRenameItems => MenuBinding?.AllowRename ?? true;

    /// <summary>
    /// Gets or sets whether a tap (a single click) opens the item, as in a phone's file manager: set from
    /// <c>HackFileView.OpenOnTap</c> (ADR 0171 §2). Off by default: a click selects, a double click opens.
    /// </summary>
    internal bool TapOpens { get; set; }

    /// <summary>
    /// Gets or sets whether a tap adds the item to the selection or removes it from it, instead of opening it:
    /// the selection mode of the mobile window (ADR 0171 §2), which has no Ctrl key.
    /// </summary>
    internal bool TapTogglesSelection { get; set; }

    /// <summary>The second click of a double click lands on the folder just opened: it must not open it too.</summary>
    private static readonly TimeSpan TapRepeatGuard = TimeSpan.FromMilliseconds(450);

    private DateTimeOffset _lastTapOpen = DateTimeOffset.MinValue;

    /// <summary>
    /// A click (or a tap) on an item: selects it, adds it with Ctrl; with <see cref="TapOpens"/>, opens it,
    /// and with <see cref="TapTogglesSelection"/>, adds it to the selection or removes it.
    /// </summary>
    internal Task ClickItemAsync(FileViewItem item, bool ctrlKey)
    {
        if (TapTogglesSelection)
        {
            if (item.IsSelected)
            {
                DeselectItemInternal(item);
            }
            else
            {
                SelectItemInternal(item, additive: true);
            }

            return Task.CompletedTask;
        }

        if (!TapOpens || ctrlKey)
        {
            SelectItemInternal(item, ctrlKey);
            return Task.CompletedTask;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastTapOpen < TapRepeatGuard)
        {
            return Task.CompletedTask;
        }

        _lastTapOpen = now;
        return ActivateItemAsync(item);
    }

    /// <summary>A double click opens the item, unless a tap already did (<see cref="TapOpens"/>).</summary>
    internal Task DoubleClickItemAsync(FileViewItem item) =>
        TapOpens || TapTogglesSelection ? Task.CompletedTask : ActivateItemAsync(item);

    /// <summary>Gets the directory currently displayed.</summary>
    public VirtualPath CurrentDirectory { get; private set; }

    /// <summary>Gets whether the current directory has been read once: before that, <see cref="Items"/> is empty only because nothing is loaded.</summary>
    internal bool IsLoaded => _lastSnapshot is not null;

    /// <summary>
    /// Gets or sets the text shown when <see cref="Filter"/> hides every entry of a folder that has some,
    /// e.g. <c>No results for "x"</c> from the explorer's search box; a generic text otherwise.
    /// </summary>
    internal string? NoMatchText { get; set; }

    /// <summary>
    /// Gets the message drawn inside the list when it shows nothing (ADR 0185 §2): the folder is empty, or
    /// the filter hides everything; <see langword="null"/> while loading, when there are items, and on
    /// the desktop and in the tree, which draw no such message.
    /// </summary>
    internal string? EmptyText
    {
        get
        {
            if (_lastSnapshot is null || Items.Count > 0 || Mode is FileViewMode.Desktop or FileViewMode.Tree)
            {
                return null;
            }

            return _lastSnapshot.Entries.Count == 0 ? L["Empty.Folder"] : NoMatchText ?? L["Empty.NoMatch"];
        }
    }

    /// <summary>Gets the current directory's items.</summary>
    public IReadOnlyList<FileViewItem> Items { get; private set; } = [];

    /// <summary>Gets the primary selected item, or <see langword="null"/> when nothing is selected.</summary>
    public FileViewItem? SelectedItem { get; private set; }

    /// <summary>Gets every currently selected item.</summary>
    public IReadOnlyList<FileViewItem> SelectedItems { get; private set; } = [];

    /// <summary>Gets the effective icon provider for this instance: <see cref="IconProvider"/> if set, else the default one.</summary>
    internal IShellIconProvider EffectiveIconProvider => IconProvider ?? DefaultIconProvider;

    /// <summary>Raised before navigating to a new directory. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewNavigatingEventArgs>? Navigating;

    /// <summary>Raised after navigation completes.</summary>
    public event EventHandler<FileViewPathChangedEventArgs>? OnPathChange;

    /// <summary>Raised before an item is activated. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewOpeningEventArgs>? Opening;

    /// <summary>Raised after an item has been activated.</summary>
    public event EventHandler<FileViewOpenedEventArgs>? Opened;

    /// <summary>Raised before an inline rename commits. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewRenamingEventArgs>? Renaming;

    /// <summary>Raised after an inline rename commits.</summary>
    public event EventHandler<FileViewRenamedEventArgs>? Renamed;

    /// <summary>Raised before a control-initiated create proceeds. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewCreatingEventArgs>? Creating;

    /// <summary>Raised after a control-initiated create completes.</summary>
    public event EventHandler<FileViewCreatedEventArgs>? Created;

    /// <summary>Raised before a control-initiated delete proceeds. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewDeletingEventArgs>? Deleting;

    /// <summary>Raised after a control-initiated delete completes.</summary>
    public event EventHandler<FileViewDeletedEventArgs>? Deleted;

    /// <summary>Raised before a move (drag-drop or rename) proceeds. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewMovingEventArgs>? Moving;

    /// <summary>Raised after a move completes.</summary>
    public event EventHandler<FileViewMovedEventArgs>? Moved;

    /// <summary>Raised before a drag-drop copy proceeds. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewCopyingEventArgs>? Copying;

    /// <summary>Raised after a drag-drop copy completes.</summary>
    public event EventHandler<FileViewCopiedEventArgs>? Copied;

    /// <summary>Raised before a selection change takes effect. Set <c>e.Cancel = true</c> to veto.</summary>
    public event EventHandler<FileViewSelectionChangingEventArgs>? SelectionChanging;

    /// <summary>Raised after the selection changes.</summary>
    public event EventHandler<FileViewSelectionChangedEventArgs>? SelectionChanged;

    /// <summary>Raised when a context menu is about to open, after provider customization has run.</summary>
    public event EventHandler<FileViewContextMenuOpeningEventArgs>? ContextMenuOpening;

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            await NavigateAsync(InitialDirectory);
        }

        ApplyCutPaths();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && AllowDragDrop && JavaScript is not null)
        {
            // Loaded up front so the first dragstart can write its payload synchronously, and so drops
            // are stashed by the native listener before Blazor's handler reads them.
            await AttachDropZoneAsync(_rootElement);
        }

        if (firstRender && JavaScript is not null && (ClipboardRequested.HasDelegate || DropRequested.HasDelegate))
        {
            // Ctrl+V is read from the browser's paste event rather than the keydown (ADR 0065): the event
            // comes after the host clipboard bridge updated the OS clipboard, and carries host files.
            IJSObjectReference module = await EnsureJsModuleAsync();
            _pasteCallbacks ??= DotNetObjectReference.Create(this);
            _pasteTargetAttached = await module.InvokeAsync<bool>("attachPasteTarget", _rootElement, _pasteCallbacks);
        }

        if (firstRender && JavaScript is not null)
        {
            // ADR 0185 §16: the bubble after a pause of the mouse on an item (never on touch).
            IJSObjectReference module = await EnsureJsModuleAsync();
            _pasteCallbacks ??= DotNetObjectReference.Create(this);
            await module.InvokeAsync<bool>("attachTooltips", _rootElement, _pasteCallbacks, FileViewTooltip.DelayMilliseconds);
        }

        if (_pendingReveal is { } reveal && JavaScript is not null)
        {
            _pendingReveal = null;
            IJSObjectReference module = await EnsureJsModuleAsync();
            await module.InvokeAsync<bool>("revealItem", _rootElement, reveal);
        }
    }

    private void ApplyCutPaths()
    {
        foreach (FileViewItem item in _items)
        {
            item.IsCut = CutPaths is not null && CutPaths.Contains(item.FullPath.Value);
        }
    }

    /// <summary>Navigates this instance to <paramref name="path"/>.</summary>
    public async Task NavigateAsync(VirtualPath path, CancellationToken cancellationToken = default)
    {
        FileViewNavigatingEventArgs navigatingArgs = new(CurrentDirectory, path);
        Navigating?.Invoke(this, navigatingArgs);
        if (navigatingArgs.Cancel)
        {
            return;
        }

        FileSystemResult<FileSystemDirectorySnapshot> result = await FileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(path), cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not enumerate '{path.Value}': {result.Error?.Code.ToString() ?? "unknown error"}.");
        }

        await AttachWatchAsync(path, cancellationToken);
        _lastSnapshot = result.Value;
        RebuildItems(result.Value!, preserveSelection: false);
        CurrentDirectory = path;
        StateHasChanged();
        OnPathChange?.Invoke(this, new FileViewPathChangedEventArgs(path));
    }

    /// <summary>Reloads the current directory's contents, preserving selection where possible (matched by name).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        FileSystemResult<FileSystemDirectorySnapshot> result = await FileSystem.EnumerateAsync(
            new FileSystemEnumerateRequest(CurrentDirectory), cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not refresh '{CurrentDirectory.Value}': {result.Error?.Code.ToString() ?? "unknown error"}.");
        }

        _lastSnapshot = result.Value;
        RebuildItems(result.Value!, preserveSelection: true);
        StateHasChanged();
    }

    /// <summary>
    /// Re-applies <see cref="Filter"/> against the most recently fetched directory listing, without a
    /// filesystem round-trip — call after changing what the delegate would return (e.g. a search box's
    /// query changed) so <see cref="Items"/> reflects it immediately. A no-op before the first successful
    /// <see cref="NavigateAsync"/>/<see cref="RefreshAsync"/>.
    /// </summary>
    public void RefreshFilter()
    {
        if (_lastSnapshot is not null)
        {
            RebuildItems(_lastSnapshot, preserveSelection: true);
            StateHasChanged();
        }
    }

    /// <summary>Clears the current selection.</summary>
    public void ClearSelect() => ChangeSelection([]);

    /// <summary>
    /// Shows one item (ADR 0096, section 1, the file manager's half of <c>RevealFileIntent</c>):
    /// navigates to its parent folder when needed, selects it alone and scrolls it into view once
    /// rendered, giving it the keyboard focus.
    /// </summary>
    /// <param name="path">Absolute path of the file or folder to show.</param>
    /// <param name="cancellationToken">Cancels the navigation.</param>
    /// <returns><see langword="true"/> when the item exists in its folder and is now selected.</returns>
    /// <exception cref="InvalidOperationException">The parent folder cannot be listed.</exception>
    public async Task<bool> RevealAsync(VirtualPath path, CancellationToken cancellationToken = default)
    {
        int separator = path.Value.LastIndexOf('/');
        if (path.Value == "/" || separator < 0)
        {
            return false;
        }

        VirtualPath parent = separator == 0 ? VirtualPath.Parse("/") : VirtualPath.Parse(path.Value[..separator]);
        if (CurrentDirectory != parent)
        {
            await NavigateAsync(parent, cancellationToken);
        }

        string name = path.Value[(separator + 1)..];
        FileViewItem? match = _items.Find(item => string.Equals(item.FileName, name, StringComparison.Ordinal));
        if (match is null)
        {
            return false;
        }

        ChangeSelection([match]);
        _pendingReveal = match.FullPath.Value;
        StateHasChanged();
        return true;
    }

    /// <summary>Selects the item with the given file name, if present in the current directory.</summary>
    public void SelectByName(string fileName)
    {
        FileViewItem? match = _items.Find(item => string.Equals(item.FileName, fileName, StringComparison.Ordinal));
        if (match is not null)
        {
            ChangeSelection([match]);
        }
    }

    private void RebuildItems(FileSystemDirectorySnapshot snapshot, bool preserveSelection)
    {
        HashSet<string> selectedNames = preserveSelection
            ? new HashSet<string>(_selection.Select(item => item.FileName), StringComparer.Ordinal)
            : [];

        // An item being renamed in place survives a reload of the same folder (the watch refreshes
        // right after "New folder" created it): the views key the typed name by the item (ADR 0175).
        FileViewItem? renaming = _items.Find(item => item.IsRenaming);
        _items.Clear();
        _selection.Clear();
        _currentDirectoryRevision = snapshot.Revision;

        foreach (FileSystemDirectoryItem entry in snapshot.Entries)
        {
            VirtualPath itemPath = CombineChild(snapshot.Path, entry.Name.Value);
            FileViewItem item = renaming is not null
                && renaming.FullPath == itemPath
                && renaming.Metadata.Revision == entry.Metadata.Revision
                ? renaming
                : new(itemPath, entry.Name.Value, entry.Metadata.Kind == FileSystemEntryKind.Directory, entry.Metadata)
                {
                    Owner = this
                };
            item.IsSelected = false;
            if (Filter is not null && !Filter(item))
            {
                continue;
            }

            item.IsCut = CutPaths is not null && CutPaths.Contains(item.FullPath.Value);
            // ADR 0160 §6: the explorer shows a shortcut with its app's name and icon.
            MenuBinding?.Decorate?.Invoke(item);

            if (selectedNames.Contains(item.FileName))
            {
                item.IsSelected = true;
                _selection.Add(item);
            }

            _items.Add(item);
        }

        Items = _items.AsReadOnly();
        SelectedItems = [.. _selection];
        SelectedItem = _selection.FirstOrDefault();
    }

    private static VirtualPath CombineChild(VirtualPath parent, string name) =>
        VirtualPath.Parse(parent.Value == "/" ? $"/{name}" : $"{parent.Value}/{name}");

    internal void SelectItemInternal(FileViewItem item, bool additive)
    {
        IReadOnlyList<FileViewItem> proposed = additive && AllowMultiSelect
            ? [.. _selection.Where(existing => existing != item), item]
            : [item];
        ChangeSelection(proposed);
    }

    internal void DeselectItemInternal(FileViewItem item)
    {
        IReadOnlyList<FileViewItem> proposed = [.. _selection.Where(existing => existing != item)];
        ChangeSelection(proposed);
    }

    /// <summary>Replaces the selection with exactly the given items in one step (e.g. Icons mode's marquee select).</summary>
    internal void SetSelectionInternal(IReadOnlyList<FileViewItem> items) => ChangeSelection(items);

    /// <summary>
    /// Roving-tabindex arrow-key navigation (<c>FV-011</c>): selects <paramref name="item"/> (replacing the
    /// current selection, matching standard single-item arrow-key movement) and moves real DOM focus to it,
    /// so the browser's own Tab order stays correct — only the active item is ever a Tab stop; arrow keys
    /// move that active item without ever leaving the control.
    /// </summary>
    internal async Task MoveActiveItemAsync(FileViewItem item)
    {
        item.Select();
        await FocusItemAsync(item);
    }

    private async Task FocusItemAsync(FileViewItem item)
    {
        IJSObjectReference module = await EnsureJsModuleAsync();
        await module.InvokeAsync<bool>("focusItem", _rootElement, item.FullPath.Value);
    }

    private void ChangeSelection(IReadOnlyList<FileViewItem> proposed)
    {
        FileViewSelectionChangingEventArgs changingArgs = new(proposed);
        SelectionChanging?.Invoke(this, changingArgs);
        if (changingArgs.Cancel)
        {
            return;
        }
        
        foreach (FileViewItem item in _selection)
        {
            item.IsSelected = false;
        }

        _selection.Clear();
        foreach (FileViewItem item in proposed)
        {
            item.IsSelected = true;
            _selection.Add(item);
        }

        SelectedItems = [.. _selection];
        SelectedItem = _selection.FirstOrDefault();
        StateHasChanged();
        SelectionChanged?.Invoke(this, new FileViewSelectionChangedEventArgs(SelectedItems));
    }

    internal void NotifyItemRenamingStateChanged(FileViewItem item) => StateHasChanged();

    /// <summary>Cancels an in-progress inline rename without touching the filesystem.</summary>
    internal void CancelRename(FileViewItem item)
    {
        item.IsRenaming = false;
        StateHasChanged();
    }

    /// <summary>Commits an in-progress inline rename: routes through the same <c>Moving</c>/<c>Moved</c> path as a drag-drop move.</summary>
    internal async Task CommitRenameAsync(FileViewItem item, string proposedName)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsRenaming)
        {
            // Already committed/canceled by a prior call (e.g. Enter's keydown handler running before the
            // input's blur handler also fires) — idempotent no-op.
            return;
        }

        if (string.IsNullOrWhiteSpace(proposedName) || string.Equals(proposedName, item.FileName, StringComparison.Ordinal))
        {
            CancelRename(item);
            return;
        }

        FileViewRenamingEventArgs renamingArgs = new(item, proposedName);
        Renaming?.Invoke(this, renamingArgs);
        if (renamingArgs.Cancel)
        {
            CancelRename(item);
            return;
        }

        VirtualPath destination = CombineChild(CurrentDirectory, proposedName);
        FileViewMovingEventArgs movingArgs = new([item], destination);
        Moving?.Invoke(this, movingArgs);
        if (movingArgs.Cancel)
        {
            CancelRename(item);
            return;
        }

        FileSystemMutationResult result = await FileSystem.MoveAsync(
            new FileSystemMoveRequest(
                item.FullPath, destination, item.Metadata.Revision, _currentDirectoryRevision, _currentDirectoryRevision));

        item.IsRenaming = false;
        if (result.Transaction.Status != FileSystemTransactionStatus.Committed)
        {
            StateHasChanged();
            return;
        }

        string previousName = item.FileName;
        await RefreshAsync();
        FileViewItem? renamedItem = _items.Find(candidate => string.Equals(candidate.FileName, proposedName, StringComparison.Ordinal));
        if (renamedItem is not null)
        {
            Moved?.Invoke(this, new FileViewMovedEventArgs([renamedItem], CurrentDirectory));
            Renamed?.Invoke(this, new FileViewRenamedEventArgs(renamedItem, previousName));
        }
    }

    /// <summary>Activates one item (double-click, Enter, or the context menu's default action).</summary>
    internal async Task ActivateItemAsync(FileViewItem item)
    {
        FileViewOpeningEventArgs openingArgs = new(item, FolderActivation);
        Opening?.Invoke(this, openingArgs);
        if (openingArgs.Cancel)
        {
            return;
        }

        if (item.IsDirectory)
        {
            switch (FolderActivation)
            {
                case FileViewFolderActivationMode.Navigate:
                    await NavigateAsync(item.FullPath);
                    break;
                case FileViewFolderActivationMode.NewWindow:
                    if (Intents is null)
                    {
                        throw new InvalidOperationException(
                            "FolderActivation is NewWindow but no Intents gateway was supplied.");
                    }

                    await Intents.OpenFileAsync(item.FullPath, mediaType: "inode/directory");
                    break;
                case FileViewFolderActivationMode.Custom:
                    if (OnCustomFolderActivate is null)
                    {
                        throw new InvalidOperationException(
                            "FolderActivation is Custom but no OnCustomFolderActivate callback was supplied.");
                    }

                    await OnCustomFolderActivate(item);
                    break;
            }
        }

        Opened?.Invoke(this, new FileViewOpenedEventArgs(item));
    }

    /// <summary>
    /// Deletes every currently selected item via the control's own context menu/keyboard shortcut,
    /// or hands them to <see cref="DeleteRequested"/> when the host supplied it.
    /// </summary>
    /// <param name="permanent">Whether the user asked for a permanent deletion (Shift+Delete).</param>
    internal async Task DeleteSelectedAsync(bool permanent = false)
    {
        if (_selection.Count == 0 || MenuBinding is { AllowDelete: false })
        {
            return;
        }

        FileViewItem[] targets = [.. _selection];

        FileViewDeletingEventArgs deletingArgs = new(targets);
        Deleting?.Invoke(this, deletingArgs);
        if (deletingArgs.Cancel)
        {
            return;
        }

        if (DeleteRequested.HasDelegate)
        {
            await DeleteRequested.InvokeAsync(new FileViewDeleteRequestedEventArgs(targets, permanent));
            return;
        }

        List<VirtualPath> deleted = [];
        foreach (FileViewItem item in targets)
        {
            FileSystemMutationResult result = await FileSystem.DeleteAsync(
                new FileSystemDeleteRequest(item.FullPath, item.Metadata.Revision, _currentDirectoryRevision));
            if (result.Transaction.Status == FileSystemTransactionStatus.Committed)
            {
                deleted.Add(item.FullPath);
            }
        }

        await RefreshAsync();
        if (deleted.Count > 0)
        {
            Deleted?.Invoke(this, new FileViewDeletedEventArgs(deleted));
        }
    }

    /// <summary>Copies the given items into <paramref name="destinationDirectory"/> (e.g. from drag-drop with a copy modifier held).</summary>
    internal async Task CopyItemsAsync(IReadOnlyList<FileViewItem> items, VirtualPath destinationDirectory)
    {
        if (items.Count == 0)
        {
            return;
        }

        FileViewCopyingEventArgs copyingArgs = new(items, destinationDirectory);
        Copying?.Invoke(this, copyingArgs);
        if (copyingArgs.Cancel)
        {
            return;
        }

        FileSystemResult<FileSystemEntrySnapshot> destinationStat = await FileSystem.StatAsync(
            new FileSystemStatRequest(destinationDirectory));
        if (!destinationStat.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not stat destination '{destinationDirectory.Value}': {destinationStat.Error?.Code.ToString() ?? "unknown error"}.");
        }

        long destinationRevision = destinationStat.Value!.Metadata.Revision;
        List<FileViewItem> succeeded = [];
        foreach (FileViewItem item in items)
        {
            VirtualPath destinationPath = CombineChild(destinationDirectory, item.FileName);
            FileSystemMutationResult result = await FileSystem.CopyAsync(
                new FileSystemCopyRequest(item.FullPath, destinationPath, item.Metadata.Revision, destinationRevision));
            if (result.Transaction.Status == FileSystemTransactionStatus.Committed)
            {
                succeeded.Add(item);
            }
        }

        if (Equals(destinationDirectory, CurrentDirectory))
        {
            await RefreshAsync();
        }

        if (succeeded.Count > 0)
        {
            Copied?.Invoke(this, new FileViewCopiedEventArgs(succeeded, destinationDirectory));
        }
    }

    /// <summary>
    /// Moves the given items into <paramref name="destinationDirectory"/> (drag-drop without a copy
    /// modifier held). Unlike <see cref="CopyItemsAsync"/>, a move mutates each item's source parent
    /// directory too, so — unlike every other mutation method here, which assumes its items came from
    /// <see cref="Items"/> (this instance's own current directory) — this one re-derives each item's own
    /// parent, because a drag-drop source item may belong to a different <see cref="FileView"/> instance
    /// (inter-control drag) with a directory this instance has never enumerated.
    /// </summary>
    internal async Task MoveItemsAsync(IReadOnlyList<FileViewItem> items, VirtualPath destinationDirectory)
    {
        if (items.Count == 0)
        {
            return;
        }

        FileViewMovingEventArgs movingArgs = new(items, destinationDirectory);
        Moving?.Invoke(this, movingArgs);
        if (movingArgs.Cancel)
        {
            return;
        }

        FileSystemResult<FileSystemEntrySnapshot> destinationStat = await FileSystem.StatAsync(
            new FileSystemStatRequest(destinationDirectory));
        if (!destinationStat.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not stat destination '{destinationDirectory.Value}': {destinationStat.Error?.Code.ToString() ?? "unknown error"}.");
        }

        long destinationRevision = destinationStat.Value!.Metadata.Revision;
        bool touchesCurrentDirectory = Equals(destinationDirectory, CurrentDirectory);
        List<FileViewItem> succeeded = [];
        foreach (FileViewItem item in items)
        {
            VirtualPath sourceParent = ParentOf(item.FullPath);
            FileSystemResult<FileSystemEntrySnapshot> sourceParentStat = await FileSystem.StatAsync(
                new FileSystemStatRequest(sourceParent));
            if (!sourceParentStat.Succeeded)
            {
                continue;
            }

            VirtualPath destinationPath = CombineChild(destinationDirectory, item.FileName);
            FileSystemMutationResult result = await FileSystem.MoveAsync(
                new FileSystemMoveRequest(
                    item.FullPath, destinationPath, item.Metadata.Revision, sourceParentStat.Value!.Metadata.Revision, destinationRevision));
            if (result.Transaction.Status == FileSystemTransactionStatus.Committed)
            {
                succeeded.Add(item);
                touchesCurrentDirectory |= Equals(sourceParent, CurrentDirectory);
            }
        }

        if (touchesCurrentDirectory)
        {
            await RefreshAsync();
        }

        if (succeeded.Count > 0)
        {
            Moved?.Invoke(this, new FileViewMovedEventArgs(succeeded, destinationDirectory));
        }
    }

    private static VirtualPath ParentOf(VirtualPath path)
    {
        string value = path.Value;
        int lastSlash = value.LastIndexOf('/');
        return VirtualPath.Parse(lastSlash <= 0 ? "/" : value[..lastSlash]);
    }

    /// <summary>
    /// Starts a native HTML5 drag for <paramref name="item"/> (drag-drop, <c>FV-006</c>): if <paramref name="item"/>
    /// isn't already selected it becomes the sole selection (matching common OS behavior — dragging an
    /// unselected row drags just that row), then the resulting selection is serialized onto the native
    /// <c>DataTransfer</c> via <c>FileView.razor.js</c>.
    /// </summary>
    internal async Task OnItemDragStartAsync(FileViewItem item, DragEventArgs args)
    {
        if (!AllowDragDrop)
        {
            return;
        }

        if (!item.IsSelected)
        {
            item.Select();
        }

        string? json = BuildDragPayloadJson(SelectedItems);
        if (json is null)
        {
            return;
        }

        IJSObjectReference module = await EnsureJsModuleAsync();
        await module.InvokeAsync<bool>("setDragData", DragMimeType, json);
    }

    /// <summary>
    /// Starts a drag from the desktop (ADR 0160 §3): the files of <paramref name="items"/> as any drag of
    /// the view, so another window can receive them, plus <paramref name="desktopJson"/>, the keys of the
    /// icons moved, so the desktop knows the drag is its own. Both are written in one call, while the
    /// browser's <c>dragstart</c> is still being dispatched.
    /// </summary>
    internal async Task StartDesktopDragAsync(IReadOnlyList<FileViewItem> items, string desktopJson)
    {
        if (!AllowDragDrop)
        {
            return;
        }

        Dictionary<string, string> data = new(StringComparer.Ordinal) { [DesktopItemMimeType] = desktopJson };
        if (BuildDragPayloadJson(items) is { } json)
        {
            data[DragMimeType] = json;
        }

        IJSObjectReference module = await EnsureJsModuleAsync();
        await module.InvokeAsync<bool>("setDragDataMany", data);
    }

    /// <summary>
    /// Completes a drop onto a directory item (drag-drop, <c>FV-006</c>): ignored for non-directory
    /// targets (the drop then reaches the background, i.e. the current directory). See
    /// <see cref="DropOntoAsync"/>.
    /// </summary>
    internal Task OnItemDropAsync(FileViewItem target, DragEventArgs args) =>
        !AllowDragDrop || !target.IsDirectory ? Task.CompletedTask : DropOntoAsync(target.FullPath, args);

    /// <summary>A drop on the background of the view, or on a file item: targets the current directory.</summary>
    internal Task OnBackgroundDropAsync(DragEventArgs args) =>
        !AllowDragDrop ? Task.CompletedTask : DropOntoAsync(CurrentDirectory, args);

    /// <summary>
    /// Completes a drop onto <paramref name="destination"/> (a folder, the background, a tree node or a
    /// breadcrumb segment; ADR 0058, section 1). With <see cref="DropRequested"/>, the dropped HackerOS
    /// entries and host files go to the host; otherwise Ctrl held routes through
    /// <see cref="CopyItemsAsync"/> and a plain drop through <see cref="MoveItemsAsync"/>, both raising the
    /// cancelable <see cref="Moving"/>/<see cref="Copying"/> events, and host files are ignored.
    /// </summary>
    internal async Task DropOntoAsync(VirtualPath destination, DragEventArgs args)
    {
        FileViewDropRequestedEventArgs? drop = await ReadDropAsync(destination, args);
        if (drop is not null)
        {
            await CompleteDropAsync(destination, drop);
        }
    }

    /// <summary>
    /// The second half of <see cref="DropOntoAsync"/>, for a drop already read with <see cref="ReadDropAsync"/>
    /// (the desktop reads it first to place the new icons under the pointer, ADR 0160 §3).
    /// </summary>
    internal async Task CompleteDropAsync(VirtualPath destination, FileViewDropRequestedEventArgs drop)
    {
        try
        {
            if (DropRequested.HasDelegate)
            {
                await DropRequested.InvokeAsync(drop);
                return;
            }

            List<FileViewItem> resolved = [];
            foreach (FileViewDraggedEntry entry in drop.Entries)
            {
                FileSystemResult<FileSystemEntrySnapshot> stat = await FileSystem.StatAsync(new FileSystemStatRequest(entry.Path));
                if (stat.Succeeded)
                {
                    resolved.Add(new FileViewItem(entry.Path, HackFileOperations.GetName(entry.Path.Value), entry.IsDirectory, stat.Value!.Metadata) { Owner = this });
                }
            }

            if (resolved.Count == 0)
            {
                return;
            }

            if (drop.Copy)
            {
                await CopyItemsAsync(resolved, destination);
            }
            else
            {
                await MoveItemsAsync(resolved, destination);
            }
        }
        finally
        {
            await ReleaseHostFilesAsync(drop);
        }
    }

    /// <summary>
    /// Reads what was dropped (the stash of <c>FileView.razor.js</c>): the HackerOS drag payload, whose
    /// entries are checked to exist and never include <paramref name="destination"/> itself, and the
    /// files of a drop from the host system. <see langword="null"/> when nothing usable was dropped.
    /// </summary>
    /// <param name="destination">Target directory, or <see langword="null"/> for a drop zone without one.</param>
    /// <param name="args">The Blazor drop event.</param>
    internal async Task<FileViewDropRequestedEventArgs?> ReadDropAsync(VirtualPath? destination, DragEventArgs args)
    {
        if (!AllowDragDrop || args.DataTransfer?.Types is not { } types)
        {
            return null;
        }

        bool internalDrag = types.Contains(DragMimeType);
        bool hostFiles = types.Contains("Files");
        bool appDrag = types.Contains(AppDragMimeType);
        if (!internalDrag && !hostFiles && !appDrag)
        {
            return null;
        }

        IJSObjectReference module = await EnsureJsModuleAsync();
        List<FileViewDraggedEntry> entries = [];
        if (internalDrag)
        {
            string? json = await module.InvokeAsync<string?>("getDragData", DragMimeType);
            FileViewDragEnvelope? envelope = null;
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    envelope = JsonSerializer.Deserialize(json, FileViewDragEnvelopeJsonSerializerContext.Default.FileViewDragEnvelope);
                }
                catch (JsonException)
                {
                    envelope = null;
                }
            }

            if (envelope is not null)
            {
                foreach (VirtualFileDragPayload file in envelope.Files)
                {
                    await AddDraggedEntryAsync(file.FilePath, isDirectory: false, destination, entries);
                }

                foreach (VirtualFolderDragPayload folder in envelope.Folders)
                {
                    await AddDraggedEntryAsync(folder.FolderPath, isDirectory: true, destination, entries);
                }
            }
        }

        IReadOnlyList<string> appIds = appDrag ? ReadAppIds(await module.InvokeAsync<string?>("getDragData", AppDragMimeType)) : [];
        (IReadOnlyList<HackHostFile> files, int token) = hostFiles ? await TakeHostFilesAsync(module) : ([], 0);
        FileViewDropRequestedEventArgs drop = new(destination, entries, files, args.CtrlKey) { HostFileToken = token, AppIds = appIds };
        if (drop.IsEmpty)
        {
            await ReleaseHostFilesAsync(drop);
            return null;
        }

        return drop;
    }

    /// <summary>Reads the app ids of an app drag, <c>{"appId":"…"}</c> or <c>{"appIds":[…]}</c>; nothing for anything else.</summary>
    /// <param name="json">The payload of <see cref="AppDragMimeType"/>.</param>
    internal static IReadOnlyList<string> ReadAppIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            List<string> ids = [];
            if (document.RootElement.TryGetProperty("appId", out JsonElement single) && single.ValueKind == JsonValueKind.String)
            {
                ids.Add(single.GetString()!);
            }

            if (document.RootElement.TryGetProperty("appIds", out JsonElement many) && many.ValueKind == JsonValueKind.Array)
            {
                ids.AddRange(many.EnumerateArray().Where(static id => id.ValueKind == JsonValueKind.String).Select(static id => id.GetString()!));
            }

            return [.. ids.Where(static id => id.Length > 0).Distinct(StringComparer.Ordinal)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Takes the host files stashed by <c>FileView.razor.js</c> (by the last drop or paste), whose content
    /// is only read when imported.
    /// </summary>
    private async Task<(IReadOnlyList<HackHostFile> Files, int Token)> TakeHostFilesAsync(IJSObjectReference module)
    {
        DroppedHostFiles dropped = await module.InvokeAsync<DroppedHostFiles>("takeDroppedFiles");
        List<HackHostFile> files = [];
        for (int index = 0; index < dropped.Files.Count; index++)
        {
            DroppedHostFile meta = dropped.Files[index];
            int fileIndex = index;
            files.Add(new HackHostFile(
                meta.Name,
                meta.Size,
                string.IsNullOrEmpty(meta.Type) ? null : meta.Type,
                (maxBytes, cancellationToken) => OpenDroppedFileAsync(dropped.Token, fileIndex, maxBytes, cancellationToken)));
        }

        return (files, dropped.Token);
    }

    /// <summary>
    /// Called by <c>FileView.razor.js</c> for a paste (Ctrl+V) on the view (ADR 0065). Files pasted from
    /// the host clipboard are handed to <see cref="DropRequested"/> as a copy into the current
    /// directory, exactly like a drop from the host; otherwise the paste goes to
    /// <see cref="ClipboardRequested"/>, which reads the OS clipboard.
    /// </summary>
    /// <param name="hostFiles">Whether the paste carried host files the clipboard bridge accepted.</param>
    [JSInvokable]
    public async Task OnPasteShortcutAsync(bool hostFiles)
    {
        // Called from JavaScript, outside any frame: enter the app's frame first (ADR 0161 §1).
        using IDisposable? scope = AppContext is { } context ? HackExecutionContext.Enter(context) : null;
        if (!hostFiles)
        {
            await RequestClipboardAsync(FileViewClipboardAction.Paste, CurrentDirectory);
            return;
        }

        IJSObjectReference module = await EnsureJsModuleAsync();
        (IReadOnlyList<HackHostFile> files, int token) = await TakeHostFilesAsync(module);
        FileViewDropRequestedEventArgs paste = new(CurrentDirectory, [], files, copy: true) { HostFileToken = token };
        try
        {
            if (!paste.IsEmpty && DropRequested.HasDelegate)
            {
                await DropRequested.InvokeAsync(paste);
            }
        }
        finally
        {
            await ReleaseHostFilesAsync(paste);
        }
    }

    /// <summary>Forgets the host File objects of a drop once its import is over.</summary>
    internal async Task ReleaseHostFilesAsync(FileViewDropRequestedEventArgs drop)
    {
        if (drop.HostFileToken != 0 && _jsModule is not null)
        {
            await _jsModule.InvokeVoidAsync("releaseDroppedFiles", drop.HostFileToken);
        }
    }

    /// <summary>Installs the native drag listeners of <c>FileView.razor.js</c> on an element (root, breadcrumb, drop zone).</summary>
    internal async Task AttachDropZoneAsync(ElementReference element)
    {
        IJSObjectReference module = await EnsureJsModuleAsync();
        await module.InvokeAsync<bool>("attachDropZone", element, DragMimeType);
    }

    private async Task<Stream> OpenDroppedFileAsync(int token, int index, long maxBytes, CancellationToken cancellationToken)
    {
        IJSObjectReference module = await EnsureJsModuleAsync();
        IJSStreamReference reference = await module.InvokeAsync<IJSStreamReference>("openDroppedFile", cancellationToken, token, index);
        return await reference.OpenReadStreamAsync(maxBytes, cancellationToken);
    }

    private async Task AddDraggedEntryAsync(string pathValue, bool isDirectory, VirtualPath? destination, List<FileViewDraggedEntry> entries)
    {
        VirtualPath path;
        try
        {
            path = VirtualPath.Parse(pathValue);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (destination is { } target && Equals(path, target))
        {
            // Dropping an item directly onto itself: a no-op, not an error.
            return;
        }

        FileSystemResult<FileSystemEntrySnapshot> stat = await FileSystem.StatAsync(new FileSystemStatRequest(path));
        if (stat.Succeeded)
        {
            entries.Add(new FileViewDraggedEntry(path, isDirectory));
        }
    }

    /// <summary>Shape returned by <c>takeDroppedFiles</c> in <c>FileView.razor.js</c>.</summary>
    internal sealed record DroppedHostFiles(
        [property: System.Text.Json.Serialization.JsonPropertyName("token")] int Token,
        [property: System.Text.Json.Serialization.JsonPropertyName("files")] IReadOnlyList<DroppedHostFile> Files);

    /// <summary>One host file's metadata, as <c>takeDroppedFiles</c> returns it.</summary>
    internal sealed record DroppedHostFile(
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size,
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string? Type);

    /// <summary>
    /// Keyboard shortcuts handled for the whole view (bubbled from any item, or from the focused
    /// background): Ctrl+A selects every item; Ctrl+X, Ctrl+C and Ctrl+V go to <see cref="ClipboardRequested"/>.
    /// </summary>
    internal async Task OnRootKeyDownAsync(KeyboardEventArgs args)
    {
        if (!(args.CtrlKey || args.MetaKey) || args.AltKey)
        {
            return;
        }

        switch (args.Key.ToLowerInvariant())
        {
            case "a" when AllowMultiSelect:
                ChangeSelection([.. _items]);
                break;
            case "x":
                await RequestClipboardAsync(FileViewClipboardAction.Cut, CurrentDirectory);
                break;
            case "c":
                await RequestClipboardAsync(FileViewClipboardAction.Copy, CurrentDirectory);
                break;
            case "v" when !_pasteTargetAttached:
                // With FileView.razor.js attached, the paste event drives Ctrl+V (OnPasteShortcutAsync).
                await RequestClipboardAsync(FileViewClipboardAction.Paste, CurrentDirectory);
                break;
        }
    }

    /// <summary>Hands a cut, copy (of the selection) or paste (into <paramref name="destination"/>) to the host.</summary>
    internal Task RequestClipboardAsync(FileViewClipboardAction action, VirtualPath destination)
    {
        if (!ClipboardRequested.HasDelegate)
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<FileViewItem> items = action == FileViewClipboardAction.Paste ? [] : [.. _selection];
        if (action != FileViewClipboardAction.Paste && items.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ClipboardRequested.InvokeAsync(new FileViewClipboardRequestedEventArgs(action, items, destination));
    }

    private string? BuildDragPayloadJson(IReadOnlyList<FileViewItem> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        List<VirtualFileDragPayload> files = [];
        List<VirtualFolderDragPayload> folders = [];
        string sourceAppId = OwnerAppId ?? string.Empty;
        foreach (FileViewItem item in items)
        {
            if (item.IsDirectory)
            {
                // ItemCount is decorative only (drop-side logic never reads it) — not worth an extra
                // enumerate-per-dragged-folder round trip just to populate it accurately.
                folders.Add(new VirtualFolderDragPayload(sourceAppId, item.FullPath.Value, item.FileName, ItemCount: -1));
            }
            else
            {
                long size = item.Metadata is FileMetadata file ? file.Length : 0;
                string? mimeType = item.Metadata is FileMetadata fileMetadata ? fileMetadata.MediaType : null;
                files.Add(new VirtualFileDragPayload(sourceAppId, item.FullPath.Value, item.FileName, size, mimeType));
            }
        }

        return JsonSerializer.Serialize(
            new FileViewDragEnvelope(files, folders), FileViewDragEnvelopeJsonSerializerContext.Default.FileViewDragEnvelope);
    }

    private async Task<IJSObjectReference> EnsureJsModuleAsync() =>
        _jsModule ??= await JavaScript.InvokeAsync<IJSObjectReference>(
            "import", "./_content/HackerOs.Apps.FileExplorer/FileControl/FileView.razor.js");

    /// <summary>
    /// Gets whether the first folder has been read: until then <see cref="CurrentDirectory"/> is not
    /// set, and a folder created would land at the root (a toolbar clicked while it loads, ADR 0175).
    /// </summary>
    internal bool HasListing => _lastSnapshot is not null;

    /// <summary>
    /// Gets the lines of the hover bubble of the item at <paramref name="path"/> (ADR 0185 §16), counting a
    /// folder's entries; <see langword="null"/> when the item is no longer listed or is being renamed.
    /// </summary>
    [JSInvokable]
    public async Task<string[]?> GetItemTooltipAsync(string path)
    {
        FileViewItem? item = Items.FirstOrDefault(candidate => candidate.FullPath.Value == path);
        if (item is null || item.IsRenaming)
        {
            return null;
        }

        int? childCount = null;
        if (item.IsDirectory)
        {
            try
            {
                FileSystemResult<FileSystemDirectorySnapshot> listing = await FileSystem.EnumerateAsync(new FileSystemEnumerateRequest(item.FullPath));
                childCount = listing.Succeeded ? listing.Value!.Entries.Count : null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
            {
                // The bubble goes without the count.
            }
        }

        return [.. FileViewTooltip.Describe(item, childCount, L)];
    }

    /// <summary>Creates a new empty folder in the current directory via the control's own context menu.</summary>
    internal async Task CreateFolderAsync(string name)
    {
        if (!HasListing)
        {
            return;
        }

        VirtualPath path = CombineChild(CurrentDirectory, name);
        FileViewCreatingEventArgs creatingArgs = new(CurrentDirectory, name, FileSystemEntryKind.Directory);
        Creating?.Invoke(this, creatingArgs);
        if (creatingArgs.Cancel)
        {
            return;
        }

        FileSystemMutationResult result = await FileSystem.CreateAsync(
            new FileSystemCreateRequest(
                path, FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0x01FF), _currentDirectoryRevision));
        if (result.Transaction.Status != FileSystemTransactionStatus.Committed)
        {
            return;
        }

        await RefreshAsync();
        FileViewItem? created = _items.Find(candidate => string.Equals(candidate.FileName, name, StringComparison.Ordinal));
        if (created is not null)
        {
            Created?.Invoke(this, new FileViewCreatedEventArgs(created));
        }
    }

    /// <summary>
    /// The default "New Folder" context-menu action: creates a uniquely named folder, then selects and
    /// enters inline rename on it — the same imperative sequence the control's own scripting surface uses
    /// (<c>ClearSelect()</c>/<c>Select()</c>/<c>Rename()</c>).
    /// </summary>
    internal async Task CreateFolderInteractiveAsync()
    {
        // The default name follows the app's language, as Nautilus names it "Nouveau dossier" in French.
        string baseName = L["Menu.NewFolder.DefaultName"];
        string name = baseName;
        int suffix = 2;
        while (_items.Any(item => string.Equals(item.FileName, name, StringComparison.Ordinal)))
        {
            name = $"{baseName} ({suffix++})";
        }

        await CreateFolderAsync(name);
        ClearSelect();
        SelectByName(name);
        SelectedItem?.Rename();
    }

    internal void RaiseContextMenuOpening(FileViewContextMenuOpeningEventArgs args) => ContextMenuOpening?.Invoke(this, args);

    /// <summary>
    /// A right click on an item: with the OS menu, only the selection follows (an item outside the
    /// selection becomes the selection, as in Windows) and the OS script opens the menu; otherwise the
    /// view's own menu opens.
    /// </summary>
    internal Task OnItemContextMenuAsync(MouseEventArgs args, FileViewItem item)
    {
        if (!UsesOwnMenu)
        {
            if (!item.IsSelected)
            {
                item.Select();
            }

            return Task.CompletedTask;
        }

        item.Select();
        return OpenItemContextMenuAsync(args, item);
    }

    internal Task OpenItemContextMenuAsync(MouseEventArgs args, FileViewItem item) =>
        _contextMenu is null
            ? Task.CompletedTask
            : _contextMenu.OpenAsync(args, item.IsDirectory ? FileViewContextMenuScope.Directory : FileViewContextMenuScope.File, item);

    private async Task AttachWatchAsync(VirtualPath path, CancellationToken cancellationToken)
    {
        if (Watch is null)
        {
            return;
        }

        _watchLoopCts?.Cancel();
        _watchLoopCts?.Dispose();
        if (_watchSubscription is not null)
        {
            await _watchSubscription.DisposeAsync();
            _watchSubscription = null;
        }

        _watchSubscription = await Watch.WatchAsync(path, FileSystemWatchScope.ImmediateChildren, cancellationToken);
        _watchLoopCts = new CancellationTokenSource();
        _ = WatchLoopAsync(_watchSubscription, _watchLoopCts.Token);
    }

    private async Task WatchLoopAsync(ITopicChannelSubscription<FileSystemChangeEvent> subscription, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (TopicMessage<FileSystemChangeEvent> _ in subscription.Reader.ReadAllAsync(cancellationToken))
            {
                await InvokeAsync(async () => await RefreshAsync(cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the watched directory changes or the component is disposed.
        }
    }

    internal Task OnBackgroundContextMenuAsync(MouseEventArgs args) =>
        _contextMenu is null ? Task.CompletedTask : _contextMenu.OpenAsync(args, FileViewContextMenuScope.Background, item: null);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _watchLoopCts?.Cancel();
        _watchLoopCts?.Dispose();
        if (_watchSubscription is not null)
        {
            await _watchSubscription.DisposeAsync();
        }

        if (_jsModule is not null)
        {
            await _jsModule.DisposeAsync();
        }

        _pasteCallbacks?.Dispose();
    }
}
