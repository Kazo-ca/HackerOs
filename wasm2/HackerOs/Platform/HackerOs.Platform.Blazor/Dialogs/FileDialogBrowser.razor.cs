using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.Platform.Blazor.ContextMenus;
using HackerOs.Platform.Core.Shell;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;

namespace HackerOs.Platform.Blazor.Dialogs;

/// <summary>
/// The file view shared by the Open, Save and Choose folder dialogs (ADR 0157): the OS file control
/// (<c>hackeros.file-view</c>, ADR 0148 §2) of whichever app provides it, shown with the requesting
/// app's rights (ADR 0148 §4), or <see cref="VirtualFolderBrowser"/> when no app provides it.
/// </summary>
/// <remarks>
/// <para>
/// The dialogs only talk to this component: they get the folder shown, the selection (already
/// narrowed to the kinds they accept) and the files the user activated, whichever view is behind it.
/// The control is reached through the contract only, never the File Explorer by name, so an
/// administrator's choice of another provider (ADR 0156) reaches the dialogs too.
/// </para>
/// <para>
/// The OS dialog window is not an app window: nothing cascades the requesting app's context or its
/// context menus there. This component does both, so the control runs in the app's ambient scope
/// (gateways, rights, language) and its file menu opens like in any app window (ADR 0150 §4).
/// </para>
/// </remarks>
public sealed partial class FileDialogBrowser
{
    private VirtualFolderBrowser? _fallback;
    private IHackFileView? _controller;
    private ContextMenuScope? _menus;
    private VirtualPath _currentDirectory = VirtualPath.Parse("/");
    private HackFileViewMode _mode = HackFileViewMode.Details;
    private string? _error;
    private bool _initialized;
    private bool _openOnTap;

    // Built once: HackFileView hands its provider a new parameter set only when a value changes.
    private Func<HackFileViewItem, bool> _filter = default!;
    private EventCallback<HackFileViewMode> _modeChanged;
    private EventCallback<HackFileViewOpeningEventArgs> _opening;
    private EventCallback<string> _navigated;
    private EventCallback<IReadOnlyList<HackFileViewItem>> _selectionChanged;
    private EventCallback<string> _errorOccurred;
    private EventCallback<IHackFileView?> _controllerChanged;
    private Func<FileSystemDirectoryItem, bool> _fallbackFilter = default!;
    private EventCallback<FileSystemDirectorySnapshot> _fallbackDirectoryChanged;
    private EventCallback<FileSystemDirectoryItem> _fallbackFileActivated;

    /// <summary>
    /// Gets or sets the requesting app's context: the control lists, creates and renames with its
    /// rights. <see langword="null"/> shows the platform's browser over <see cref="FileSystem"/>.
    /// </summary>
    [Parameter]
    public IAppExecutionContext? Context { get; set; }

    /// <summary>Gets or sets the requesting app's filesystem, read by the platform's browser.</summary>
    [Parameter, EditorRequired]
    public IAppFileSystemGateway FileSystem { get; set; } = null!;

    /// <summary>Gets or sets the folder shown first.</summary>
    [Parameter]
    public VirtualPath InitialDirectory { get; set; } = VirtualPath.Parse("/");

    /// <summary>Gets or sets whether regular files count in the selection.</summary>
    [Parameter]
    public bool AllowFileSelection { get; set; } = true;

    /// <summary>Gets or sets whether folders count in the selection.</summary>
    [Parameter]
    public bool AllowDirectorySelection { get; set; }

    /// <summary>Gets or sets whether more than one item can be selected.</summary>
    [Parameter]
    public bool AllowMultipleSelection { get; set; }

    /// <summary>
    /// Gets or sets what the control may do beside browsing (ADR 0155 §3): <see cref="HackFileViewFeatures.None"/>
    /// in an Open dialog, new folders and renaming in a Save or Choose folder dialog. The platform's
    /// browser only browses.
    /// </summary>
    [Parameter]
    public HackFileViewFeatures Features { get; set; } = HackFileViewFeatures.None;

    /// <summary>
    /// Gets or sets which regular files are shown, from their name and media type
    /// (<see langword="null"/> when unknown); folders are always shown. Call
    /// <see cref="RefreshFilter"/> when its answer changes.
    /// </summary>
    [Parameter]
    public Func<string, string?, bool>? FileFilter { get; set; }

    /// <summary>
    /// Gets or sets whether one tap opens a folder or takes a file, with no double click (ADR 0171):
    /// <see langword="null"/> follows the active platform, on for the mobile one.
    /// </summary>
    [Parameter]
    public bool? OpenOnTap { get; set; }

    /// <summary>Raised after each navigation with the folder shown.</summary>
    [Parameter]
    public EventCallback<VirtualPath> DirectoryChanged { get; set; }

    /// <summary>Raised with the selection, narrowed to the kinds the dialog accepts.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<VirtualPath>> SelectionChanged { get; set; }

    /// <summary>
    /// Raised when a regular file is activated (double click, Enter, the menu's Open): an Open dialog
    /// opens it, a Save dialog takes its name, as GTK and Windows file choosers do. The control does
    /// not open it in an app.
    /// </summary>
    [Parameter]
    public EventCallback<VirtualPath> FileActivated { get; set; }

    /// <summary>The OS context menus; absent in a host without the platform shell (a test page).</summary>
    [Inject]
    private IServiceProvider Services { get; set; } = default!;

    /// <summary>Reads the folder shown again (after the dialog created a folder in it).</summary>
    public Task RefreshAsync() =>
        _controller is not null ? _controller.RefreshAsync()
        : _fallback is not null ? _fallback.RefreshAsync()
        : Task.CompletedTask;

    /// <summary>Selects <paramref name="path"/> in the folder shown, reading it again first; does nothing in the platform's browser.</summary>
    /// <param name="path">The item to select.</param>
    public async Task RevealAsync(VirtualPath path)
    {
        if (_controller is null)
        {
            await RefreshAsync();
            return;
        }

        await _controller.RefreshAsync();
        await _controller.RevealAsync(path.Value);
    }

    /// <summary>Applies <see cref="FileFilter"/> again (the dialog's file type changed).</summary>
    public void RefreshFilter()
    {
        _controller?.RefreshFilter();
        StateHasChanged();
    }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _filter = item => item.IsDirectory || FileFilter?.Invoke(item.Name, item.MediaType) != false;
        _modeChanged = EventCallback.Factory.Create<HackFileViewMode>(this, SetMode);
        _opening = EventCallback.Factory.Create<HackFileViewOpeningEventArgs>(this, OnOpeningAsync);
        _navigated = EventCallback.Factory.Create<string>(this, OnNavigatedAsync);
        _selectionChanged = EventCallback.Factory.Create<IReadOnlyList<HackFileViewItem>>(this, OnSelectionChangedAsync);
        _errorOccurred = EventCallback.Factory.Create<string>(this, message => _error = message);
        _controllerChanged = EventCallback.Factory.Create<IHackFileView?>(this, OnControllerChanged);
        _fallbackFilter = entry => FileFilter?.Invoke(entry.Name.Value, (entry.Metadata as FileMetadata)?.MediaType) != false;
        _fallbackDirectoryChanged = EventCallback.Factory.Create<FileSystemDirectorySnapshot>(this, OnFallbackNavigatedAsync);
        _fallbackFileActivated = EventCallback.Factory.Create<FileSystemDirectoryItem>(
            this,
            entry => FileActivated.InvokeAsync(Combine(_currentDirectory, entry.Name.Value)));
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _openOnTap = OpenOnTap ?? IsMobilePlatform();

        // The dialog re-renders on every shell render while it is open: seed the folder once only.
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _currentDirectory = InitialDirectory;
        if (Context is not null && Services.GetService(typeof(ContextMenuService)) is ContextMenuService menus)
        {
            // The dialog's own menus, in the requesting app's context: the control's file menu
            // (Open, New folder, Rename, Refresh) runs with that app's rights, as in its windows.
            _menus = menus.CreateScope(Context);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _menus?.Dispose();
        _menus = null;
    }

    private void OnControllerChanged(IHackFileView? controller)
    {
        _controller = controller;
        if (controller is not null && !string.IsNullOrEmpty(controller.CurrentDirectory))
        {
            _currentDirectory = VirtualPath.Parse(controller.CurrentDirectory);
        }
    }

    /// <summary>A file activated: the dialog takes it instead of the control opening it in its default app.</summary>
    private async Task OnOpeningAsync(HackFileViewOpeningEventArgs args)
    {
        if (args.Item.IsDirectory)
        {
            // Not handled: the control enters the folder.
            return;
        }

        args.Handled = true;
        await FileActivated.InvokeAsync(VirtualPath.Parse(args.Item.Path));
    }

    private async Task OnNavigatedAsync(string directory)
    {
        _error = null;
        _currentDirectory = VirtualPath.Parse(directory);
        await DirectoryChanged.InvokeAsync(_currentDirectory);
    }

    private Task OnFallbackNavigatedAsync(FileSystemDirectorySnapshot snapshot)
    {
        _currentDirectory = snapshot.Path;
        return DirectoryChanged.InvokeAsync(snapshot.Path);
    }

    private Task OnSelectionChangedAsync(IReadOnlyList<HackFileViewItem> items)
    {
        VirtualPath[] selection = [.. items
            .Where(item => item.IsDirectory ? AllowDirectorySelection : AllowFileSelection)
            .Select(item => VirtualPath.Parse(item.Path))];
        return SelectionChanged.InvokeAsync(selection);
    }

    private async Task NavigateAsync(VirtualPath directory)
    {
        if (_controller is null)
        {
            return;
        }

        try
        {
            await _controller.NavigateAsync(directory.Value);
        }
        catch (InvalidOperationException exception)
        {
            _error = exception.Message;
        }
    }

    private Task NavigateUpAsync()
    {
        string value = _currentDirectory.Value;
        int separator = value.LastIndexOf('/');
        return NavigateAsync(VirtualPath.Parse(separator <= 0 ? "/" : value[..separator]));
    }

    private void SetMode(HackFileViewMode mode) => _mode = mode;

    private bool IsMobilePlatform() =>
        Services.GetService(typeof(UiPlatformPreferenceService)) is UiPlatformPreferenceService preference
        && preference.Current.ActivePlatform == WellKnownAppPlatforms.Mobile;

    private static VirtualPath Combine(VirtualPath directory, string name) =>
        VirtualPath.Parse(directory.Value == "/" ? $"/{name}" : $"{directory.Value}/{name}");
}
