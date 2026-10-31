using System.Runtime.CompilerServices;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Clipboard;
using HackerOs.Apps.FileExplorer.Archives;
using HackerOs.Apps.FileExplorer.FileControl;
using HackerOs.Apps.FileExplorer.FileControl.Events;
using HackerOs.AppSdk.Files;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// The File Explorer's provider of <c>hackeros.file-view</c> (ADR 0148, ADR 0155): the explorer
/// registers it in its manifest, every app shows it through <c>HackFileView</c>, and the explorer
/// shows it itself, which makes it the one source of truth of the file control.
/// </summary>
/// <remarks>
/// <para>
/// It renders <see cref="FileView"/>, the explorer's implementation, and does by itself everything the
/// explorer used to do around it (ADR 0155 §3): the trash and permanent deletion, the session
/// clipboard, the transfers with their name conflicts, opening with the default app or "Open with",
/// and the file menus of the OS (plan §7) in place of the view's own <c>MudMenu</c>.
/// </para>
/// <para>
/// Everything runs with the <b>host</b>'s rights (ADR 0148 §4): the gateways are those of
/// <c>HostContext</c>, and the callbacks run in the host's ambient
/// context (the window's dispatch, <see cref="HackComponentBase"/>), so an app sees no file more by
/// showing the explorer's control.
/// </para>
/// </remarks>
public sealed partial class ExplorerFileView : IHackFileView
{
    private readonly FileViewMenuBinding _binding = new();
    private readonly string _menuName = "hackeros.file-view." + Guid.NewGuid().ToString("N");
    private readonly ConditionalWeakTable<FileViewItem, HackFileViewItem> _snapshots = new();
    private readonly ConditionalWeakTable<HackFileViewItem, FileViewItem> _items = new();

    /// <summary>Copies, moves, deletions and pastes in flight (ADR 0069): a closing window waits for them.</summary>
    private readonly HashSet<Task> _operations = [];

    private FileView? _view;
    private FileView? _subscribedView;
    private VirtualPath _initialDirectory = VirtualPath.Parse("/");
    private bool _initialDirectorySet;
    private Func<FileViewItem, bool> _filter = default!;
    private Func<FileViewItem, Task> _activateFolder = default!;
    private EventCallback<FileViewMode> _modeChanged;
    private EventCallback<FileViewDeleteRequestedEventArgs> _deleteRequested;
    private EventCallback<FileViewDropRequestedEventArgs> _dropRequested;
    private EventCallback<FileViewClipboardRequestedEventArgs> _clipboardRequested;
    private bool _canPaste;
    private IReadOnlyCollection<string> _cutPaths = [];
    private bool _subscribedToClipboard;
    private IDisposable? _menu;
    private bool _controllerSent;
    private IStringLocalizer<Strings>? _localizer;
    private IAppFileSystemGateway? _fileSystem;
    private FileSystemSelectedResourceHandle? _fileSystemHandle;
    private bool _fileSystemBrowsesArchives;

    /// <summary>Gets or sets the absolute path of the folder shown first.</summary>
    [Parameter]
    public string InitialDirectory { get; set; } = "/";

    /// <summary>Gets or sets the layout.</summary>
    [Parameter]
    public HackFileViewMode Mode { get; set; } = HackFileViewMode.Details;

    /// <summary>Gets or sets the callback told when the user changes the layout.</summary>
    [Parameter]
    public EventCallback<HackFileViewMode> ModeChanged { get; set; }

    /// <summary>Gets or sets what the control does by itself.</summary>
    [Parameter]
    public HackFileViewFeatures Features { get; set; } = HackFileViewFeatures.All;

    /// <summary>Gets or sets whether several items can be selected at once.</summary>
    [Parameter]
    public bool AllowMultiSelect { get; set; } = true;

    /// <summary>Gets or sets whether one tap opens an item, as on a phone (ADR 0171).</summary>
    [Parameter]
    public bool OpenOnTap { get; set; }

    /// <summary>Gets or sets which items are shown.</summary>
    [Parameter]
    public Func<HackFileViewItem, bool>? Filter { get; set; }

    /// <summary>Gets or sets what runs before an item opens.</summary>
    [Parameter]
    public EventCallback<HackFileViewOpeningEventArgs> Opening { get; set; }

    /// <summary>Gets or sets the callback told the new folder after each navigation.</summary>
    [Parameter]
    public EventCallback<string> Navigated { get; set; }

    /// <summary>Gets or sets the callback told the new selection.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<HackFileViewItem>> SelectionChanged { get; set; }

    /// <summary>Gets or sets the callback told after a rename in place.</summary>
    [Parameter]
    public EventCallback<HackFileViewRenamedEventArgs> Renamed { get; set; }

    /// <summary>Gets or sets the callback told after items went to the trash or were deleted.</summary>
    [Parameter]
    public EventCallback<HackFileViewDeletedEventArgs> Deleted { get; set; }

    /// <summary>Gets or sets what runs before a file menu is drawn.</summary>
    [Parameter]
    public EventCallback<HackFileMenuOpeningEventArgs> ContextMenuOpening { get; set; }

    /// <summary>Gets or sets the callback receiving the control's errors; notifications otherwise.</summary>
    [Parameter]
    public EventCallback<string> ErrorOccurred { get; set; }

    /// <summary>Gets or sets the grant of a folder picked in the OS folder dialog, applied to the listing.</summary>
    [Parameter]
    public FileSystemSelectedResourceHandle? SelectedResource { get; set; }

    /// <summary>The OS dialogs of the host window: confirmations, and the provided file dialogs (ADR 0155 §5).</summary>
    [Inject]
    private IDialogService? Dialogs { get; set; }

    /// <summary>
    /// Gets the explorer's own view, for the explorer window's chrome (breadcrumb, drop zones on its
    /// places): the window and this control are the same app, so it may reach the implementation.
    /// </summary>
    internal FileView? View => _view;

    /// <summary>The explorer's texts in the language of the app that shows the control (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= HostContext.GetLocalizer<Strings>();

    /// <summary>
    /// The host's filesystem, narrowed to the folder picked in a dialog when the host passed its grant;
    /// rebuilt only when the grant changes, so the view is not re-rendered for a new gateway each time.
    /// </summary>
    private IAppFileSystemGateway FileSystem
    {
        get
        {
            if (_fileSystem is null || !Equals(_fileSystemHandle, SelectedResource))
            {
                _fileSystemHandle = SelectedResource;
                _fileSystem = SelectedResource is { } handle ? HostContext.FileSystem.WithSelectedHandle(handle) : HostContext.FileSystem;
                _fileSystemBrowsesArchives = false;
            }

            // ADR 0185 §6-9: only the explorer's own windows see inside the archives.
            if (BrowsesArchives && !_fileSystemBrowsesArchives)
            {
                _fileSystemBrowsesArchives = true;
                _fileSystem = new ZipArchiveFileSystemGateway(_fileSystem);
            }

            return _fileSystem;
        }
    }

    private FileViewMode ViewMode => Mode switch
    {
        HackFileViewMode.Icons => FileViewMode.Icons,
        HackFileViewMode.Tree => FileViewMode.Tree,
        HackFileViewMode.Desktop => FileViewMode.Desktop,
        _ => FileViewMode.Details
    };

    private EventCallback<FileViewClipboardRequestedEventArgs> ClipboardCallback =>
        Has(HackFileViewFeatures.Clipboard) ? _clipboardRequested : default;

    // ---- IHackFileView -------------------------------------------------------------------------------

    /// <inheritdoc />
    public string CurrentDirectory => _view?.CurrentDirectory.Value ?? _initialDirectory.Value;

    /// <inheritdoc />
    public IReadOnlyList<HackFileViewItem> Items => _view is null ? [] : [.. _view.Items.Select(Snapshot)];

    /// <inheritdoc />
    public IReadOnlyList<HackFileViewItem> SelectedItems => _view is null ? [] : [.. _view.SelectedItems.Select(Snapshot)];

    /// <inheritdoc />
    public bool CanPaste => _canPaste && Has(HackFileViewFeatures.Clipboard);

    /// <inheritdoc />
    public bool IsBusy => _operations.Count > 0;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public Task NavigateAsync(string path, CancellationToken cancellationToken = default) =>
        _view is null ? Task.CompletedTask : _view.NavigateAsync(VirtualPath.Parse(path), cancellationToken);

    /// <inheritdoc />
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        _view is null ? Task.CompletedTask : _view.RefreshAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> RevealAsync(string path, CancellationToken cancellationToken = default) =>
        _view is null ? Task.FromResult(false) : _view.RevealAsync(VirtualPath.Parse(path), cancellationToken);

    /// <inheritdoc />
    public void ClearSelection() => _view?.ClearSelect();

    /// <inheritdoc />
    public void RefreshFilter() => _view?.RefreshFilter();

    /// <inheritdoc />
    public bool CanExecute(string command)
    {
        if (_view is null)
        {
            return false;
        }

        IReadOnlyList<FileViewItem> selection = _view.SelectedItems;
        return command switch
        {
            _ when IsInArchive && ArchiveWriteCommands.Contains(command) => false,
            HackFileMenuItems.Open => selection.Count > 0,
            HackFileMenuItems.OpenWithOther => selection.Count == 1 && Has(HackFileViewFeatures.OpenWith),
            HackFileMenuItems.Cut or HackFileMenuItems.Copy => selection.Count > 0 && Has(HackFileViewFeatures.Clipboard),
            HackFileMenuItems.Paste => CanPaste,
            HackFileMenuItems.PasteInto => CanPaste && selection is [{ IsDirectory: true }],
            HackFileMenuItems.Rename => selection.Count == 1 && Has(HackFileViewFeatures.Rename),
            HackFileMenuItems.Delete or HackFileMenuItems.DeletePermanently => selection.Count > 0 && Has(HackFileViewFeatures.Delete),
            HackFileMenuItems.NewFolder => Has(HackFileViewFeatures.CreateFolder) && _view.HasListing,
            HackFileMenuItems.Refresh => true,
            HackFileMenuItems.Properties => selection.Count <= 1 && Has(HackFileViewFeatures.Properties),
            _ => false
        };
    }

    /// <inheritdoc />
    public Task ExecuteAsync(string command)
    {
        if (_view is null || !CanExecute(command))
        {
            return Task.CompletedTask;
        }

        // The toolbar and the menu run the same commands: the menu's targets are the selection here.
        return RunMenuCommandAsync(command, [.. _view.SelectedItems], _view.CurrentDirectory);
    }

    /// <inheritdoc />
    public Task WhenIdleAsync() => _operations.Count == 0 ? Task.CompletedTask : Task.WhenAll([.. _operations]);

    // ---- Lifecycle -----------------------------------------------------------------------------------

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _filter = MatchesHostFilter;
        _activateFolder = ActivateFolderAsync;
        _modeChanged = EventCallback.Factory.Create<FileViewMode>(this, OnModeChangedAsync);
        _deleteRequested = EventCallback.Factory.Create<FileViewDeleteRequestedEventArgs>(this, e => DeleteItemsAsync(e.Items, e.Permanent));
        _dropRequested = EventCallback.Factory.Create<FileViewDropRequestedEventArgs>(this, OnDropRequestedAsync);
        _clipboardRequested = EventCallback.Factory.Create<FileViewClipboardRequestedEventArgs>(this, OnClipboardRequestedAsync);
        _binding.Attach = AttachMenu;
        // An empty menu: each opening fills its own copy for the item right-clicked (ADR 0155 §4).
        _menu = Menus.Define(_menuName, static _ => { }, OnMenuOpeningAsync);
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        SetInitialDirectory();
        _binding.AllowRename = Has(HackFileViewFeatures.Rename);
        _binding.AllowDelete = Has(HackFileViewFeatures.Delete);
        ConfigureDesktop();
    }

    /// <summary>Reads <see cref="InitialDirectory"/> once; an empty or invalid value starts at <c>/</c>.</summary>
    private void SetInitialDirectory()
    {
        if (_initialDirectorySet)
        {
            return;
        }

        _initialDirectorySet = true;
        try
        {
            _initialDirectory = VirtualPath.Parse(string.IsNullOrWhiteSpace(InitialDirectory) ? "/" : InitialDirectory);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            _initialDirectory = VirtualPath.Parse("/");
        }
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        // Blazor renders FileView at the first await below, before OnParametersSet runs: its start
        // folder must be known by then, or it lists "/" (which the desktop may not read).
        SetInitialDirectory();
        // ADR 0203: the selected icon pack draws the files and shortcuts from the first listing.
        await StartIconPackAsync();
        if (!Has(HackFileViewFeatures.Clipboard))
        {
            return;
        }

        try
        {
            // Another window (or app) may cut or copy at any time: follow the session clipboard.
            HostContext.Clipboard.Changed += OnClipboardChanged;
            _subscribedToClipboard = true;
        }
        catch (NotSupportedException)
        {
            // A host without a clipboard (a test context): nothing to paste, ever.
        }

        await ReadClipboardStateAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_view is not null)
        {
            _view.TapOpens = OpenOnTap;
        }

        if (!ReferenceEquals(_subscribedView, _view))
        {
            Unsubscribe();
            if (_view is not null)
            {
                _view.OnPathChange += OnViewPathChanged;
                _view.SelectionChanged += OnViewSelectionChanged;
                _view.Opened += OnViewItemOpened;
                _view.Renamed += OnViewItemRenamed;
                _subscribedView = _view;
            }
        }

        await StartDesktopAsync();

        if (!_controllerSent && _view is not null)
        {
            _controllerSent = true;
            await ControllerReady.InvokeAsync(this);
            RaiseStateChanged();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Unsubscribe();
        _menu?.Dispose();
        if (_subscribedToClipboard)
        {
            HostContext.Clipboard.Changed -= OnClipboardChanged;
            _subscribedToClipboard = false;
        }

        await StopDesktopAsync();
        await StopIconPackAsync();
    }

    private void Unsubscribe()
    {
        if (_subscribedView is null)
        {
            return;
        }

        _subscribedView.OnPathChange -= OnViewPathChanged;
        _subscribedView.SelectionChanged -= OnViewSelectionChanged;
        _subscribedView.Opened -= OnViewItemOpened;
        _subscribedView.Renamed -= OnViewItemRenamed;
        _subscribedView = null;
    }

    // ---- View events ---------------------------------------------------------------------------------

    private void OnViewPathChanged(object? sender, FileViewPathChangedEventArgs e)
    {
        // Inside an archive the view renames and deletes nothing by itself (ADR 0185 §9).
        _binding.AllowRename = Has(HackFileViewFeatures.Rename) && !IsInArchive;
        _binding.AllowDelete = Has(HackFileViewFeatures.Delete) && !IsInArchive;
        RaiseStateChanged();
        if (Navigated.HasDelegate)
        {
            _ = InvokeAsync(() => Navigated.InvokeAsync(e.Path.Value));
        }
    }

    private void OnViewSelectionChanged(object? sender, FileViewSelectionChangedEventArgs e)
    {
        RaiseStateChanged();
        if (SelectionChanged.HasDelegate)
        {
            IReadOnlyList<HackFileViewItem> selection = [.. e.Selection.Select(Snapshot)];
            _ = InvokeAsync(() => SelectionChanged.InvokeAsync(selection));
        }
    }

    /// <summary>A file activated (double click, Enter): the host may open it itself, else its default app opens it.</summary>
    private void OnViewItemOpened(object? sender, FileViewOpenedEventArgs e)
    {
        if (!e.Item.IsDirectory)
        {
            _ = InvokeAsync(() => OpenFileAsync(e.Item));
        }
    }

    private void OnViewItemRenamed(object? sender, FileViewRenamedEventArgs e)
    {
        RaiseStateChanged();
        if (Renamed.HasDelegate)
        {
            string newPath = e.Item.FullPath.Value;
            string oldPath = Combine(ParentOf(newPath), e.PreviousName);
            _ = InvokeAsync(() => Renamed.InvokeAsync(new HackFileViewRenamedEventArgs(oldPath, newPath)));
        }
    }

    private Task OnModeChangedAsync(FileViewMode mode) => ModeChanged.InvokeAsync(mode switch
    {
        FileViewMode.Icons => HackFileViewMode.Icons,
        FileViewMode.Tree => HackFileViewMode.Tree,
        FileViewMode.Desktop => HackFileViewMode.Desktop,
        _ => HackFileViewMode.Details
    });

    /// <summary>
    /// A folder activated: the host may handle it; on a desktop it opens in a new explorer window
    /// (ADR 0160 §7); elsewhere the view enters it.
    /// </summary>
    private async Task ActivateFolderAsync(FileViewItem item)
    {
        if (await RaiseOpeningAsync(item))
        {
            return;
        }

        if (IsDesktop)
        {
            try
            {
                await HostContext.Intents.OpenFileAsync(item.FullPath, "inode/directory", HostContext.CancellationToken);
            }
            catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
            {
                await ReportErrorAsync(L["Error.OpenDenied"]);
            }

            return;
        }

        try
        {
            await _view!.NavigateAsync(item.FullPath);
        }
        catch (InvalidOperationException exception)
        {
            await ReportErrorAsync(exception.Message);
        }
    }

    /// <summary>Asks the host first; returns whether it opened the item itself.</summary>
    private async Task<bool> RaiseOpeningAsync(FileViewItem item)
    {
        if (!Opening.HasDelegate)
        {
            return false;
        }

        HackFileViewOpeningEventArgs args = new(Snapshot(item));
        await Opening.InvokeAsync(args);
        return args.Handled;
    }

    private bool MatchesHostFilter(FileViewItem item) => Filter is not { } filter || filter(Snapshot(item));

    private bool Has(HackFileViewFeatures feature) => (Features & feature) == feature;

    // ---- Snapshots -----------------------------------------------------------------------------------

    /// <summary>
    /// The SDK's snapshot of an item. The same view item always gives the same snapshot, so the token
    /// <c>Menus.Attach</c> gives its element stays the same from one render to the next (ADR 0154 §3).
    /// </summary>
    private HackFileViewItem Snapshot(FileViewItem item) => _snapshots.GetValue(item, CreateSnapshot);

    private HackFileViewItem CreateSnapshot(FileViewItem item)
    {
        HackFileViewItem snapshot = new(
            item.FullPath.Value,
            item.FileName,
            item.IsDirectory,
            item.Metadata is FileMetadata file ? file.Length : 0,
            item.IsDirectory ? "inode/directory" : item.Metadata is FileMetadata media ? media.MediaType : "application/octet-stream",
            item.Metadata.Timestamps.ContentModifiedAtUtc);
        _items.AddOrUpdate(snapshot, item);
        return snapshot;
    }

    private IReadOnlyDictionary<string, object> AttachMenu(FileViewItem? item) =>
        Menus.Attach(_menuName, item is null ? null : Snapshot(item));

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    // ---- Errors --------------------------------------------------------------------------------------

    /// <summary>Hands an error to the host, or posts it as a notification when the host has no handler.</summary>
    private async Task ReportErrorAsync(string message)
    {
        if (ErrorOccurred.HasDelegate)
        {
            await ErrorOccurred.InvokeAsync(message);
            return;
        }

        try
        {
            HostContext.Notifications.Post(NotificationSeverity.Warning, L["Notification.Title"], message);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            // The host may not post notifications: the error stays unseen, as a refused notification would.
        }
    }

    private static string ParentOf(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Combine(string parent, string name) => parent == "/" ? $"/{name}" : $"{parent}/{name}";
}
