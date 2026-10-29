using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.Apps.FileExplorer.FileControl;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// The File Explorer's mobile entry point (ADR 0171): <see cref="FileExplorerWindow"/>'s state and commands
/// in a phone layout. Only the presentation is its own: the search field, the selection mode, the bottom
/// sheets and the system Back button.
/// </summary>
public partial class FileExplorerMobileWindow
{
    /// <summary>The bottom sheet shown, if any.</summary>
    private enum MobileSheet
    {
        None,
        New,
        Selection,
        Menu
    }

    private readonly string _uploadInputId = $"explorer-upload-{Guid.NewGuid():N}";
    private MobileSheet _sheet;
    private bool _searching;
    private bool _selecting;
    private bool _lastCanNavigateBack;

    /// <summary>The top bar's title: the trash's name or the current folder's (<c>/</c> for the root).</summary>
    private string FolderName
    {
        get
        {
            if (_showTrash)
            {
                return L["Places.Trash"];
            }

            string path = _state.CurrentPath.TrimEnd('/');
            return path.Length == 0 ? "/" : path[(path.LastIndexOf('/') + 1)..];
        }
    }

    /// <summary>The top bar's arrow leaves the trash, goes back to the previous folder, or else to the parent.</summary>
    private bool CanGoBack => _showTrash || _state.CanNavigateBack || _state.CanNavigateUp;

    /// <inheritdoc />
    /// <remarks>
    /// The system Back button closes the sheet, the search and the selection first, leaves the trash, then
    /// goes back through the folders visited; with nothing left, the OS takes over (it leaves the app).
    /// </remarks>
    public bool CanNavigateBack =>
        _sheet != MobileSheet.None || _searching || _selecting || _showTrash || _state.CanNavigateBack;

    /// <inheritdoc />
    public event Action? CanNavigateBackChanged;

    /// <inheritdoc />
    public async ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
    {
        if (_sheet != MobileSheet.None)
        {
            CloseSheet();
        }
        else if (_searching)
        {
            CloseSearch();
        }
        else if (_selecting)
        {
            EndSelection();
        }
        else if (_showTrash)
        {
            await LeaveTrashAsync();
        }
        else if (_state.CanNavigateBack)
        {
            await NavigateBackAsync();
        }
        else
        {
            return AppBackResult.NotHandled;
        }

        await InvokeAsync(StateHasChanged);
        return AppBackResult.Handled;
    }

    /// <inheritdoc />
    /// <remarks>Also tells the shell when the system Back button starts or stops having something to do.</remarks>
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        await base.OnAppAfterRenderAsync(firstRender);
        bool canNavigateBack = CanNavigateBack;
        if (canNavigateBack != _lastCanNavigateBack)
        {
            _lastCanNavigateBack = canNavigateBack;
            CanNavigateBackChanged?.Invoke();
        }
    }

    /// <summary>The control is ready: in the selection mode, a tap adds or removes an item (ADR 0171 §2).</summary>
    private async Task OnMobileControllerChangedAsync(IHackFileView? files)
    {
        await OnControllerChangedAsync(files);
        ApplyTapMode();
    }

    private void ApplyTapMode()
    {
        if (_fileView is { } view)
        {
            view.TapTogglesSelection = _selecting;
        }
    }

    private async Task OnMobileNavigated(string path)
    {
        _selecting = false;
        ApplyTapMode();
        await OnNavigated(path);
    }

    /// <summary>The selection mode ends with its last item, as on a phone.</summary>
    private void OnMobileSelectionChanged(IReadOnlyList<HackFileViewItem> selection)
    {
        if (selection.Count == 0 && _selecting)
        {
            _selecting = false;
            ApplyTapMode();
        }

        OnSelectionChanged(selection);
    }

    private Task GoBackAsync()
    {
        if (_showTrash)
        {
            return LeaveTrashAsync();
        }

        return _state.CanNavigateBack ? NavigateBackAsync() : NavigateUpAsync();
    }

    /// <summary>Back to the folder shown before the trash.</summary>
    private Task LeaveTrashAsync()
    {
        _showTrash = false;
        return Task.CompletedTask;
    }

    private void OpenSearch()
    {
        _sheet = MobileSheet.None;
        _searching = true;
    }

    /// <summary>Closing the search clears it: the whole folder comes back.</summary>
    private void CloseSearch()
    {
        _searching = false;
        if (!string.IsNullOrEmpty(_state.SearchQuery))
        {
            _state.SearchQuery = string.Empty;
            OnSearchQueryChanged();
        }
    }

    private void StartSelection()
    {
        _sheet = MobileSheet.None;
        _selecting = true;
        ApplyTapMode();
    }

    private void EndSelection()
    {
        _selecting = false;
        ApplyTapMode();
        _files?.ClearSelection();
    }

    private void SelectAll()
    {
        if (_fileView is { } view)
        {
            view.SetSelectionInternal(view.Items);
        }
    }

    private void OpenSheet(MobileSheet sheet) => _sheet = sheet;

    private void CloseSheet() => _sheet = MobileSheet.None;

    /// <summary>Closes the sheet, then runs the chosen command (its dialog opens over the window, not over the sheet).</summary>
    private Task FromSheetAsync(Func<Task> command)
    {
        CloseSheet();
        return command();
    }

    private void ChooseViewMode(FileViewMode mode)
    {
        CloseSheet();
        SetViewMode(mode);
    }

    private void ToggleHiddenFromSheet()
    {
        CloseSheet();
        ToggleHiddenFiles();
    }

}
