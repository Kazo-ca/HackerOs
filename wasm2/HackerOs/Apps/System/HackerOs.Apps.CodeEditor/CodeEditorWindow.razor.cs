using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Accessibility;
using HackerOs.AppSdk.Blazor;
using HackerOs.AppSdk.Components;
using HackerOs.AppSdk.Globalization;
using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace HackerOs.Apps.CodeEditor;

/// <summary>
/// Code-behind of the editor's workbench: tabs (one Monaco model per tab, so each keeps its undo
/// history and scroll position), the open folder (explorer, search, quick open), the terminal
/// panel, the VS Code shortcuts forwarded by the collocated module, and unsaved-buffer recovery.
/// </summary>
public partial class CodeEditorWindow
{
    private enum SideView
    {
        Explorer,
        Search
    }

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The workbench's texts in its own process's language (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    [Inject]
    private MudBlazor.IDialogService MudDialogService { get; set; } = default!;

    private CodeEditorSession _session = new();
    private ElementReference _root;
    private ElementReference _editorHost;
    private CodeEditorFileService? _fileService;
    private CodeEditorRecoveryStore? _recoveryStore;
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private CancellationTokenSource? _recoveryDebounce;
    private IJSObjectReference? _module;
    private IJSObjectReference? _editor;
    private IJSObjectReference? _workbench;
    private DotNetObjectReference<CodeEditorWindow>? _dotNetReference;
    private string? _shownDocumentId;
    private readonly List<string> _closedDocumentIds = [];
    private bool _editorSyncRequired;
    private bool _focusAfterSync;
    private CodeEditorNavigation? _pendingReveal;
    private readonly Dictionary<Guid, bool> _dirtyReports = [];
    private string? _statusMessage;
    private bool _statusIsError;
    private bool _recoveryClearedOnClose;
    private int _dirtyCount;

    private CodeEditorWorkspace? _workspace;
    private CodeEditorExplorer? _explorer;
    private CodeEditorSearchView? _search;
    private CodeEditorQuickOpenPalette? _quickOpen;
    private CodeEditorCursorStatus? _cursorStatus;
    private SideView _view = SideView.Explorer;
    private bool _sidebarVisible = true;
    private bool _minimap = true;
    private bool _wordWrap;

    private bool _showPanel;
    private DockView _dockView = DockView.Terminal;
    private readonly List<TerminalTab> _terminalTabs = [];
    private string? _activeTerminalTabId;
    private int _terminalTabCounter;
    private readonly List<StatusEntry> _statusLog = [];

    /// <summary>Most lines kept by the Status tab.</summary>
    private const int MaxStatusLog = 200;

    /// <summary>Gets or sets an optional VFS path supplied by an open-file intent.</summary>
    [Parameter]
    public string? LaunchFilePath { get; set; }

    private CodeEditorDocument? ActiveDocument => _session.ActiveDocument;

    /// <inheritdoc />
    protected override void OnAppInitialized()
    {
        _fileService = new CodeEditorFileService(AppContext.FileSystem);
        _recoveryStore = new CodeEditorRecoveryStore(
            AppContext.FileSystem,
            AppContext.UserId,
            HackUser.Describe(AppContext).HomeDirectory);
        _editorSyncRequired = true;
    }

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        // ADR 0117 §2: named before the window is first shown.
        await SetWindowTitleAsync(DocumentWindowTitle());
        CodeEditorRecoveryResult recovery = await _recoveryStore!.LoadAsync(AppContext.CancellationToken);
        if (recovery.HasSession)
        {
            _session = CodeEditorSession.Restore(recovery.Session!);
            if (_session.Documents.Count > 0)
            {
                SetStatus(L["Status.Recovered"], isError: false);
            }
        }
        else if (recovery.Status is not (CodeEditorRecoveryStatus.Missing or CodeEditorRecoveryStatus.Cancelled))
        {
            SetStatus(recovery.Error ?? L["Status.RecoveryLoadFailed"], isError: true);
        }

        // Explicit host parameter, otherwise the open-file intent's path (first launch argument).
        VirtualPath? launchFile = !string.IsNullOrWhiteSpace(LaunchFilePath) ? VirtualPath.Parse(LaunchFilePath) : LaunchFile;
        if (launchFile is { } path)
        {
            await LoadPathAsync(path, _fileService!);
        }

        UpdateDirtyCount();
    }

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        // Switching tabs, opening a folder, editing and saving all end in a render.
        await SetWindowTitleAsync(DocumentWindowTitle());

        if (firstRender)
        {
            try
            {
                // ADR 0105: Monaco draws its own text, so it takes the session's accessibility
                // preferences (high-contrast theme, text size, steady cursor) itself.
                HackAccessibilityPreferences accessibility = await AppContext.Accessibility.GetPreferencesAsync(AppContext.CancellationToken);
                _dotNetReference = DotNetObjectReference.Create(this);
                _module = await JavaScript.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/HackerOs.Apps.CodeEditor/CodeEditorWindow.razor.js");
                _editor = await _module.InvokeAsync<IJSObjectReference>(
                    "createEditor",
                    _editorHost,
                    _dotNetReference,
                    new
                    {
                        minimap = _minimap,
                        ariaLabel = L["Editor.AriaLabel"].Value,
                        highContrast = accessibility.HighContrast,
                        textScale = accessibility.TextScale,
                        reducedMotion = accessibility.ReducedMotion
                    });
                AppContext.Accessibility.Changed += OnAccessibilityChanged;
                _workbench = await _module.InvokeAsync<IJSObjectReference>(
                    "attachWorkbench", _root, _dotNetReference);
                _editorSyncRequired = true;
                _focusAfterSync = true;
            }
            catch (Exception ex) when (ex is JSException or InvalidOperationException)
            {
                SetStatus(L["Status.AssetsFailed"], isError: true);
            }
        }

        if (_editor is null)
        {
            return;
        }

        foreach (string closed in _closedDocumentIds)
        {
            await _editor.InvokeVoidAsync("closeDocument", closed);
            if (_shownDocumentId == closed)
            {
                _shownDocumentId = null;
            }
        }

        _closedDocumentIds.Clear();

        CodeEditorDocument? document = ActiveDocument;
        string? activeId = document?.Id.ToString();
        if (document is not null && (_editorSyncRequired || activeId != _shownDocumentId))
        {
            await _editor.InvokeVoidAsync("showDocument", activeId, document.Content, ModeKey(document.SyntaxMode), document.IsDirty);
            _shownDocumentId = activeId;
        }

        _editorSyncRequired = false;
        if (document is not null && _pendingReveal is { } reveal && reveal.Path == document.Path)
        {
            _pendingReveal = null;
            _focusAfterSync = false;
            await _editor.InvokeVoidAsync("reveal", reveal.Line, reveal.Column, reveal.Length);
        }

        if (_focusAfterSync && document is not null)
        {
            _focusAfterSync = false;
            await _editor.InvokeVoidAsync("focus");
        }
    }

    // ---- Calls from the collocated module -------------------------------------------------------

    /// <summary>
    /// Receives a tab's text from Monaco once typing pauses (or on <see cref="FlushEditorAsync"/>),
    /// never per keystroke: copying the whole file to .NET on each key is what made typing lag.
    /// </summary>
    [JSInvokable]
    public async Task OnEditorChanged(string documentId, string content)
    {
        // A JavaScript callback runs outside the app's ambient frame (ADR 0043, 0067): enter it first.
        using IDisposable scope = HackExecutionContext.Enter(AppContext);
        CodeEditorDocument? document = FindDocument(documentId);
        if (document is null)
        {
            return;
        }

        CodeEditorEditResult result = document.TryEdit(content);
        if (!result.Succeeded)
        {
            SetStatus(result.Error ?? L["Status.EditRejected"], isError: true);
            _editorSyncRequired = true;
            await InvokeAsync(StateHasChanged);
        }

        ScheduleRecovery();
    }

    /// <summary>
    /// Receives, at once, that a tab became modified or went back to its saved text (undo), so the
    /// tab's dot follows the typing while the text itself waits for the pause.
    /// </summary>
    [JSInvokable]
    public Task OnDirtyChanged(string documentId, bool dirty)
    {
        // A JavaScript callback runs outside the app's ambient frame (ADR 0043, 0067): enter it first.
        using IDisposable scope = HackExecutionContext.Enter(AppContext);
        if (FindDocument(documentId) is { } document)
        {
            _dirtyReports[document.Id] = dirty;
            UpdateDirtyCount();
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    /// <summary>Receives cursor moves, shown as "Ln, Col" in the status bar.</summary>
    [JSInvokable]
    public Task OnCursorChanged(int line, int column, int selectedLength)
    {
        // A JavaScript callback runs outside the app's ambient frame (ADR 0043, 0067): enter it first.
        using IDisposable scope = HackExecutionContext.Enter(AppContext);
        _cursorStatus?.Update(line, column, selectedLength);
        return Task.CompletedTask;
    }

    /// <summary>Runs a workbench shortcut caught by the module (see the table in CodeEditorWindow.razor.js).</summary>
    [JSInvokable]
    public async Task OnShortcut(string name)
    {
        // A JavaScript callback runs outside the app's ambient frame (ADR 0043, 0067): enter it first.
        using IDisposable scope = HackExecutionContext.Enter(AppContext);
        switch (name)
        {
            case "save": await SaveActiveAsync(); break;
            case "saveAs": await SaveActiveAsAsync(); break;
            case "openFile": await OpenFileAsync(); break;
            case "newFile": await NewDocumentAsync(); break;
            case "closeTab": await CloseActiveAsync(); break;
            case "quickOpen": await QuickOpenAsync(); break;
            case "search": await ShowSearchAsync(); break;
            case "explorer": ShowExplorer(); break;
            case "toggleSidebar": ToggleSidebar(); break;
            case "toggleTerminal": ToggleTerminalPanel(); break;
            case "togglePanel": TogglePanel(); break;
            case "nextTab": await CycleTabAsync(1); break;
            case "previousTab": await CycleTabAsync(-1); break;
            case "wordWrap": await ToggleWordWrap(); break;
        }

        StateHasChanged();
    }

    // ---- Documents --------------------------------------------------------------------------------

    private Task NewDocumentAsync()
    {
        _session.NewDocument();
        RequestEditorSync(focus: true);
        ClearStatus();
        ScheduleRecovery();
        return Task.CompletedTask;
    }

    private async Task OpenFileAsync()
    {
        OpenFileDialogResult result = await base.OpenFileAsync(new OpenFileDialogRequest
        {
            Title = L["Dialog.OpenSourceFile"],
            InitialDirectory = _workspace?.Root,
            Filters =
            [
                new FileDialogFilter(
                    L["Dialog.Filter.Source"],
                    [".cs", ".js", ".ts", ".html", ".css", ".json", ".md", ".txt"],
                    ["text/plain", "text/x-csharp", "text/javascript", "text/typescript", "text/html", "text/css", "application/json", "text/markdown"]),
                new FileDialogFilter(L["Dialog.Filter.All"], [], [])
            ],
            RequestedAccess = SelectedFileAccess.ReadWrite
        }, AppContext.CancellationToken);

        if (result.Status != FileDialogStatus.Selected || result.Resources.Count == 0)
        {
            return;
        }

        SelectedFileResource selected = result.Resources[0];
        CodeEditorFileService selectedService = new(
            AppContext.FileSystem.WithSelectedHandle(selected.Handle));
        await LoadPathAsync(selected.Path, selectedService);
    }

    /// <summary>Opens a file of the open folder and focuses it (explorer double click, quick open, search result).</summary>
    private Task OpenFromWorkspaceAsync(VirtualPath path) => OpenFromWorkspaceAsync(path, focus: true);

    /// <summary>Shows a file clicked once in the explorer, leaving the keyboard to the tree.</summary>
    private Task PreviewFromWorkspaceAsync(VirtualPath path) => OpenFromWorkspaceAsync(path, focus: false);

    private async Task OpenFromWorkspaceAsync(VirtualPath path, bool focus)
    {
        CodeEditorDocument? open = _session.Documents.FirstOrDefault(document => document.Path == path);
        if (open is not null)
        {
            if (_session.Activate(open.Id))
            {
                RequestEditorSync(focus);
                ScheduleRecovery();
            }

            return;
        }

        CodeEditorFileService service = _workspace is { } workspace && workspace.Contains(path)
            ? new CodeEditorFileService(workspace.FileSystem)
            : _fileService!;
        await LoadPathAsync(path, service, announce: false, focus: focus);
    }

    private async Task LoadPathAsync(VirtualPath path, CodeEditorFileService service, bool announce = true, bool focus = true)
    {
        CodeEditorLoadResult result = await service.LoadAsync(path, AppContext.CancellationToken);
        if (!result.Succeeded || result.Content is null || result.Revision is null)
        {
            if (result.Status is not CodeEditorFileStatus.Cancelled)
            {
                SetStatus(result.Error ?? L["Status.OpenFailed"], isError: true);
            }

            return;
        }

        _session.OpenDocument(path, result.Content, result.Revision.Value);
        RequestEditorSync(focus);
        if (announce)
        {
            SetStatus(L["Status.Opened", path.Value], isError: false);
        }
        else
        {
            ClearStatus();
        }

        ScheduleRecovery();
    }

    /// <summary>Goes to a search result: opens (or activates) its file, then selects the match.</summary>
    private async Task NavigateAsync(CodeEditorNavigation navigation)
    {
        _pendingReveal = navigation;
        await OpenFromWorkspaceAsync(navigation.Path);
        if (ActiveDocument?.Path != navigation.Path)
        {
            _pendingReveal = null;
        }

        StateHasChanged();
    }

    private Task OnQuickOpenPickAsync(CodeEditorWorkspaceFile file) => OpenFromWorkspaceAsync(file.Path);

    private async Task SaveActiveAsync()
    {
        if (ActiveDocument is { } document)
        {
            await SaveAsync(document);
        }
    }

    /// <summary>Saves a document to its file, or asks where (Save As) for a new buffer.</summary>
    private async Task SaveAsync(CodeEditorDocument document)
    {
        await FlushEditorAsync();
        if (document.Path is not VirtualPath path)
        {
            await SaveAsAsync(document);
            return;
        }

        // A file of the open folder is written through the folder's grant (picked in the dialog).
        CodeEditorFileService service = _workspace is { } workspace && workspace.Contains(path)
            ? new CodeEditorFileService(workspace.FileSystem)
            : _fileService!;
        await SaveDocumentAsync(document, path, service);
    }

    private async Task SaveActiveAsAsync()
    {
        if (ActiveDocument is { } document)
        {
            await SaveAsAsync(document);
        }
    }

    private async Task SaveAsAsync(CodeEditorDocument document)
    {
        SaveFileDialogResult result = await base.SaveFileAsync(new SaveFileDialogRequest
        {
            Title = L["Dialog.SaveSourceFile"],
            InitialDirectory = document.Path is { } path ? CodeEditorWorkspace.ParentOf(path) : _workspace?.Root,
            SuggestedFileName = document.Path is null ? SuggestedFileName(document) : document.Title,
            ConfirmOverwrite = true
        }, AppContext.CancellationToken);

        if (result.Status != FileDialogStatus.Selected || result.Resource is null)
        {
            return;
        }

        CodeEditorFileService selectedService = new(
            AppContext.FileSystem.WithSelectedHandle(result.Resource.Handle));
        await SaveDocumentAsync(document, result.Resource.Path, selectedService);
    }

    private async Task SaveAllAsync()
    {
        await FlushEditorAsync();
        foreach (CodeEditorDocument document in _session.Documents.Where(IsDirty).ToArray())
        {
            await SaveAsync(document);
        }
    }

    private async Task SaveDocumentAsync(
        CodeEditorDocument document,
        VirtualPath path,
        CodeEditorFileService service)
    {
        await FlushEditorAsync();
        bool isNewFile = document.Path != path;
        string savedContent = document.Content;
        CodeEditorSaveResult result = await service.SaveAsync(
            document, path, AppContext.CancellationToken);
        if (result.Succeeded)
        {
            _dirtyReports.Remove(document.Id);
            if (_editor is not null)
            {
                await _editor.InvokeVoidAsync("markSaved", document.Id.ToString(), savedContent);
            }

            SetStatus(L["Status.Saved", path.Value], isError: false);
            if (isNewFile && _explorer is not null && _workspace?.Contains(path) == true)
            {
                await _explorer.RefreshAsync();
            }
        }
        else if (result.Status is not CodeEditorFileStatus.Cancelled)
        {
            SetStatus(result.Error ?? L["Status.SaveFailed"], isError: true);
        }

        UpdateDirtyCount();
        ScheduleRecovery();
    }

    private Task ActivateDocumentAsync(Guid documentId)
    {
        if (_session.Activate(documentId))
        {
            RequestEditorSync(focus: true);
            ScheduleRecovery();
        }

        return Task.CompletedTask;
    }

    private Task CycleTabAsync(int step)
    {
        IReadOnlyList<CodeEditorDocument> documents = _session.Documents;
        if (documents.Count < 2)
        {
            return Task.CompletedTask;
        }

        int index = documents.ToList().FindIndex(document => document.Id == _session.ActiveDocumentId);
        int next = ((index < 0 ? 0 : index) + step + documents.Count) % documents.Count;
        return ActivateDocumentAsync(documents[next].Id);
    }

    /// <summary>A middle click on a tab closes it, as in VS Code.</summary>
    private Task OnTabMouseUpAsync(MouseEventArgs args, Guid documentId) =>
        args.Button == 1 ? CloseDocumentAsync(documentId) : Task.CompletedTask;

    private Task CloseActiveAsync() =>
        ActiveDocument is { } document ? CloseDocumentAsync(document.Id) : Task.CompletedTask;

    private async Task CloseAllAsync()
    {
        foreach (CodeEditorDocument document in _session.Documents.ToArray())
        {
            if (!await CloseDocumentAsync(document.Id))
            {
                return;
            }
        }
    }

    /// <summary>Closes a tab, asking first when it has unsaved changes. Returns false when the user kept it.</summary>
    private async Task<bool> CloseDocumentAsync(Guid documentId)
    {
        await FlushEditorAsync();
        CodeEditorCloseResult result = _session.Close(documentId);
        if (result == CodeEditorCloseResult.ConfirmationRequired)
        {
            CodeEditorDocument document = _session.Documents.Single(item => item.Id == documentId);
            bool? discard = await MudDialogService.ShowMessageBoxAsync(
                L["Dialog.UnsavedChanges.Title"],
                L["Dialog.UnsavedChanges.Message", document.Title],
                yesText: L["Dialog.Discard"],
                cancelText: L["Dialog.Cancel"]);
            if (discard != true)
            {
                return false;
            }

            result = _session.Close(documentId, discardDirty: true);
        }

        if (result == CodeEditorCloseResult.Closed)
        {
            _dirtyReports.Remove(documentId);
            _closedDocumentIds.Add(documentId.ToString());
            UpdateDirtyCount();
            RequestEditorSync(focus: true);
            ScheduleRecovery();
        }

        return result == CodeEditorCloseResult.Closed;
    }

    private async Task ChangeSyntaxModeAsync(ChangeEventArgs args)
    {
        if (ActiveDocument is not CodeEditorDocument document
            || !Enum.TryParse(args.Value?.ToString(), out CodeEditorSyntaxMode mode))
        {
            return;
        }

        document.SetSyntaxMode(mode);
        if (_editor is not null)
        {
            await _editor.InvokeVoidAsync("setLanguage", ModeKey(mode));
            await _editor.InvokeVoidAsync("focus");
        }

        ScheduleRecovery();
    }

    private async Task RunEditorActionAsync(string actionId)
    {
        if (_editor is not null && ActiveDocument is not null)
        {
            await _editor.InvokeVoidAsync("runAction", actionId);
        }
    }

    private async Task FocusEditorAsync()
    {
        if (_editor is not null && ActiveDocument is not null)
        {
            await _editor.InvokeVoidAsync("focus");
        }
    }

    /// <summary>The content of an open tab, so search sees unsaved edits.</summary>
    private string? OpenDocumentContent(VirtualPath path) =>
        _session.Documents.FirstOrDefault(document => document.Path == path)?.Content;

    // ---- Folder, explorer, search -----------------------------------------------------------------

    private async Task OpenFolderAsync()
    {
        VirtualPath initialDirectory = _workspace?.Root ?? VirtualPath.Parse(
            HackUser.Describe(AppContext).HomeDirectory);
        SelectFolderDialogResult result = await base.SelectFolderAsync(new SelectFolderDialogRequest
        {
            Title = L["Dialog.OpenFolder"],
            InitialDirectory = initialDirectory,
            // A new project starts in a folder made here (ADR 0175).
            AllowCreateFolder = true
        }, AppContext.CancellationToken);

        if (result.Status != FileDialogStatus.Selected || result.Resource is null)
        {
            return;
        }

        _workspace = new CodeEditorWorkspace(
            result.Resource.Path,
            AppContext.FileSystem.WithSelectedHandle(result.Resource.Handle),
            result.Resource.Handle);
        _sidebarVisible = true;
        _view = SideView.Explorer;
        ClearStatus();
        StateHasChanged();
    }

    private void CloseFolder()
    {
        _workspace = null;
        StateHasChanged();
    }

    private async Task NewFileInFolderAsync()
    {
        ShowExplorer();
        StateHasChanged();
        await Task.Yield();
        _explorer?.BeginNewFile();
    }

    private Task FindInFolderAsync(VirtualPath folder)
    {
        string include = _workspace is { } workspace && folder != workspace.Root
            ? workspace.RelativePathOf(folder) + "/**"
            : string.Empty;
        return ShowSearchAsync(include);
    }

    private Task ShowSearchAsync() => ShowSearchAsync(null);

    /// <summary>Shows the Search view and focuses its box, seeded with the editor's selection.</summary>
    private async Task ShowSearchAsync(string? include)
    {
        _sidebarVisible = true;
        _view = SideView.Search;
        string? selection = null;
        if (_editor is not null && ActiveDocument is not null)
        {
            try
            {
                selection = await _editor.InvokeAsync<string>("getSelectedText");
            }
            catch (JSException)
            {
            }
        }

        StateHasChanged();
        if (_search is not null)
        {
            await _search.FocusAsync(selection, include);
        }
    }

    private void ShowExplorer()
    {
        _sidebarVisible = true;
        _view = SideView.Explorer;
    }

    /// <summary>The activity bar: a click on the current view hides the side bar, as in VS Code.</summary>
    private void ToggleView(SideView view)
    {
        if (_sidebarVisible && _view == view)
        {
            _sidebarVisible = false;
            return;
        }

        _sidebarVisible = true;
        _view = view;
    }

    private void ToggleSidebar() => _sidebarVisible = !_sidebarVisible;

    private async Task QuickOpenAsync()
    {
        if (_workspace is null)
        {
            // Without a folder there is nothing to list; VS Code would offer recent files.
            await OpenFileAsync();
            return;
        }

        if (_quickOpen is not null)
        {
            await _quickOpen.OpenAsync();
        }
    }

    /// <summary>Tabs follow a file or folder renamed in the explorer.</summary>
    private void OnWorkspaceRenamed((VirtualPath OldPath, VirtualPath NewPath) rename)
    {
        if (_session.RelocatePaths(rename.OldPath, rename.NewPath) > 0)
        {
            RequestEditorSync(focus: false);
            ScheduleRecovery();
        }
    }

    /// <summary>The explorer tree's errors (a delete or a copy that failed) go to the status bar.</summary>
    private void OnExplorerError(string message) => SetStatus(message, isError: true);

    // ---- View options -------------------------------------------------------------------------------

    private async Task ToggleMinimap()
    {
        _minimap = !_minimap;
        if (_editor is not null)
        {
            await _editor.InvokeVoidAsync("setOptions", new { minimap = _minimap });
        }
    }

    private async Task ToggleWordWrap()
    {
        _wordWrap = !_wordWrap;
        if (_editor is not null)
        {
            await _editor.InvokeVoidAsync("setOptions", new { wordWrap = _wordWrap });
        }
    }

    // ---- Bottom dock: Status, Terminal, Problems ------------------------------------------------------

    /// <summary>Shows or hides the dock, as Ctrl+J does in VS Code.</summary>
    private void TogglePanel()
    {
        _showPanel = !_showPanel;
        if (_showPanel && _dockView == DockView.Terminal)
        {
            EnsureTerminal();
        }
    }

    /// <summary>Shows or hides the dock on its Terminal tab (Ctrl+`).</summary>
    private void ToggleTerminalPanel()
    {
        if (_showPanel && _dockView == DockView.Terminal)
        {
            _showPanel = false;
            return;
        }

        ShowDockView(DockView.Terminal);
    }

    private void ShowDockView(DockView view)
    {
        _showPanel = true;
        _dockView = view;
        if (view == DockView.Terminal)
        {
            EnsureTerminal();
        }
    }

    /// <summary>The Terminal tab always holds a shell: the first one starts when it is first shown.</summary>
    private void EnsureTerminal()
    {
        if (_terminalTabs.Count == 0)
        {
            AddTerminalTab();
        }
    }

    /// <summary>Opens a new terminal, in <paramref name="directory"/> or the open folder.</summary>
    private TerminalTab AddTerminalTab(string? directory = null)
    {
        _terminalTabCounter++;
        var tab = new TerminalTab($"terminal-{_terminalTabCounter}", _terminalTabCounter, new HackTerminalOptions
        {
            WorkingDirectory = directory ?? TerminalInitialCwd(),
            ShowBanner = false
        });
        _terminalTabs.Add(tab);
        _activeTerminalTabId = tab.Id;
        _showPanel = true;
        _dockView = DockView.Terminal;
        return tab;
    }

    private void ActivateTerminalTab(string tabId) => _activeTerminalTabId = tabId;

    private void CloseTerminalTab(string tabId)
    {
        int closedIndex = _terminalTabs.FindIndex(tab => tab.Id == tabId);
        if (closedIndex < 0)
        {
            return;
        }

        TerminalTab closed = _terminalTabs[closedIndex];
        closed.Detach();
        _terminalTabs.RemoveAt(closedIndex);
        if (_terminalTabs.Count == 0)
        {
            // Closing the last terminal hides the dock, as VS Code does; reopening starts a fresh shell.
            _activeTerminalTabId = null;
            _showPanel = false;
            return;
        }

        if (_activeTerminalTabId == tabId)
        {
            _activeTerminalTabId = _terminalTabs[Math.Min(closedIndex, _terminalTabs.Count - 1)].Id;
        }
    }

    /// <summary>
    /// The Terminal app's component handed its controller: follow its title, close the tab when
    /// its shell exits (<c>exit</c>, Ctrl+D).
    /// </summary>
    private void OnTerminalReady(TerminalTab tab, IHackTerminal? terminal)
    {
        tab.Attach(terminal,
            onChanged: () => InvokeAsync(StateHasChanged),
            onExited: () => InvokeAsync(() =>
            {
                CloseTerminalTab(tab.Id);
                StateHasChanged();
            }));
    }

    /// <summary>
    /// Explorer › Open in Integrated Terminal: the active terminal moves there with a visible
    /// <c>cd</c> when it is idle, otherwise a new terminal opens in the folder (as VS Code).
    /// </summary>
    private async Task OpenInTerminalAsync(VirtualPath folder)
    {
        TerminalTab? active = ActiveTerminalTab;
        if (_showPanel && _dockView == DockView.Terminal && active?.Terminal is { IsRunning: true, IsBusy: false } terminal)
        {
            await ChangeTerminalDirectoryAsync(terminal, folder.Value);
            await terminal.FocusAsync();
            return;
        }

        AddTerminalTab(folder.Value);
    }

    private async Task ChangeTerminalDirectoryAsync(IHackTerminal terminal, string directory)
    {
        try
        {
            HackTerminalCommandResult result = await terminal.SetWorkingDirectoryAsync(directory);
            if (result.ExitStatus != 0)
            {
                SetStatus(L["Status.TerminalCd", directory], isError: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            SetStatus(L["Status.TerminalError", exception.Message], isError: true);
        }
    }

    private TerminalTab? ActiveTerminalTab => _terminalTabs.Find(tab => tab.Id == _activeTerminalTabId);

    private async Task ClearActiveTerminalAsync()
    {
        if (ActiveTerminalTab?.Terminal is { } terminal)
        {
            await terminal.ClearAsync();
            await terminal.FocusAsync();
        }
    }

    /// <summary>New terminals start in the open folder, as VS Code's integrated terminal does.</summary>
    private string TerminalInitialCwd() =>
        _workspace?.Root.Value ?? HackUser.Describe(AppContext).HomeDirectory;

    private void ClearStatusLog() => _statusLog.Clear();

    /// <summary>A terminal tab of the dock and the controller its embedded component handed over.</summary>
    private sealed class TerminalTab(string id, int number, HackTerminalOptions options)
    {
        private Action? _onChanged;
        private Action? _onExited;

        public string Id { get; } = id;

        public int Number { get; } = number;

        /// <summary>Gets the options, created once so the embedded terminal is not re-created.</summary>
        public HackTerminalOptions Options { get; } = options;

        public IHackTerminal? Terminal { get; private set; }

        /// <summary>Gets the tab label: the running command or the directory's name, as VS Code's.</summary>
        public string Label => Terminal is { } terminal ? terminal.Title : "hsh";

        public void Attach(IHackTerminal? terminal, Action onChanged, Action onExited)
        {
            Detach();
            Terminal = terminal;
            _onChanged = onChanged;
            _onExited = onExited;
            if (terminal is not null)
            {
                terminal.StateChanged += OnStateChanged;
                terminal.Exited += OnExited;
            }

            onChanged();
        }

        public void Detach()
        {
            if (Terminal is not null)
            {
                Terminal.StateChanged -= OnStateChanged;
                Terminal.Exited -= OnExited;
            }

            Terminal = null;
        }

        private void OnStateChanged(object? sender, EventArgs e) => _onChanged?.Invoke();

        private void OnExited(object? sender, EventArgs e) => _onExited?.Invoke();
    }

    /// <summary>One line of the Status tab: what the editor reported, and when.</summary>
    private sealed record StatusEntry(DateTimeOffset Time, string Message, bool IsError);

    /// <summary>Tabs of the bottom dock.</summary>
    private enum DockView
    {
        Status,
        Terminal,
        Problems
    }

    // ---- Close guard and recovery -------------------------------------------------------------------

    /// <inheritdoc />
    public async ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await FlushEditorAsync();
        CodeEditorDocument[] dirtyDocuments = _session.Documents
            .Where(document => document.IsDirty)
            .ToArray();
        if (dirtyDocuments.Length == 0)
        {
            return await ClearRecoveryForCloseAsync(cancellationToken);
        }

        string names = string.Join(", ", dirtyDocuments.Select(document => document.Title));
        bool? discard = await MudDialogService.ShowMessageBoxAsync(
            L["Dialog.UnsavedDocuments.Title"],
            L["Dialog.UnsavedDocuments.Message", names],
            yesText: L["Dialog.DiscardAndClose"],
            cancelText: L["Dialog.KeepEditing"]);
        cancellationToken.ThrowIfCancellationRequested();
        return discard == true && await ClearRecoveryForCloseAsync(cancellationToken);
    }

    /// <summary>
    /// ADR 0069: when the OS asks the editor to stop (logout, <c>kill</c>, "End task"), the pending
    /// debounced snapshot is written at once, so the last edits survive in the recovery file the next
    /// start restores, instead of being lost with the window. Nothing is written when no tab is dirty.
    /// </summary>
    protected override async Task OnAppStopRequestedAsync(HackerOs.AppSdk.Diagnostics.HackStopRequestedEventArgs e)
    {
        _recoveryDebounce?.Cancel();
        if (_recoveryClearedOnClose || !_session.Documents.Any(document => document.IsDirty))
        {
            return;
        }

        // Not the process token: it is cancelled right after this request, and the snapshot must land.
        await PersistRecoveryAsync(CancellationToken.None);
    }

    private void ScheduleRecovery()
    {
        // Cancelled, not disposed: a save already running may still be reading its token.
        _recoveryDebounce?.Cancel();
        _recoveryDebounce = new CancellationTokenSource();
        _ = PersistRecoveryAfterDelayAsync(_recoveryDebounce.Token);
    }

    private async Task PersistRecoveryAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            // The recovery file is rewritten whole: once typing has really paused, not between words.
            await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
            await PersistRecoveryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PersistRecoveryAsync(CancellationToken cancellationToken)
    {
        if (_recoveryStore is null)
        {
            return;
        }

        await _recoveryGate.WaitAsync(cancellationToken);
        try
        {
            CodeEditorRecoveryResult result = await _recoveryStore.SaveAsync(
                _session.CaptureRecovery(), cancellationToken);
            // A snapshot superseded by a newer edit comes back Cancelled: the newer one will be
            // written instead, so nothing went wrong. Only real failures reach the error banner.
            if (result.Status is not (CodeEditorRecoveryStatus.Success or CodeEditorRecoveryStatus.Cancelled)
                && !cancellationToken.IsCancellationRequested)
            {
                await InvokeAsync(() => SetStatus(result.Error ?? L["Status.RecoverySaveFailed"], isError: true));
            }
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private async Task<bool> ClearRecoveryForCloseAsync(CancellationToken cancellationToken)
    {
        if (_recoveryStore is null)
        {
            return true;
        }

        CodeEditorRecoveryResult result = await _recoveryStore.ClearAsync(cancellationToken);
        if (result.Status == CodeEditorRecoveryStatus.Success)
        {
            _recoveryClearedOnClose = true;
            return true;
        }

        if (result.Status is not CodeEditorRecoveryStatus.Cancelled)
        {
            SetStatus(result.Error ?? L["Status.RecoveryClearFailed"], isError: true);
        }

        return false;
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private CodeEditorDocument? FindDocument(string documentId) =>
        Guid.TryParse(documentId, out Guid id) ? _session.Documents.FirstOrDefault(document => document.Id == id) : null;

    private void RequestEditorSync(bool focus)
    {
        _editorSyncRequired = true;
        _focusAfterSync |= focus;
        StateHasChanged();
    }

    private void UpdateDirtyCount() => _dirtyCount = _session.Documents.Count(IsDirty);

    /// <summary>
    /// Whether a tab shows as modified: Monaco's report when it sent one (it knows at the keystroke),
    /// else the document's own comparison (restored or just loaded tabs).
    /// </summary>
    private bool IsDirty(CodeEditorDocument document) =>
        _dirtyReports.TryGetValue(document.Id, out bool dirty) ? dirty : document.IsDirty;

    /// <summary>Brings every tab's pending text from Monaco before an operation reads it.</summary>
    private async Task FlushEditorAsync()
    {
        if (_editor is null)
        {
            return;
        }

        try
        {
            await _editor.InvokeVoidAsync("flush");
        }
        catch (JSException)
        {
        }
    }

    /// <summary>
    /// Reports a message: it is kept in the dock's Status tab, shown in the status bar, and an
    /// error is also shown above the editor until dismissed.
    /// </summary>
    private void SetStatus(string message, bool isError)
    {
        _statusLog.Add(new StatusEntry(AppContext.Clock.UtcNow, message, isError));
        if (_statusLog.Count > MaxStatusLog)
        {
            _statusLog.RemoveRange(0, _statusLog.Count - MaxStatusLog);
        }

        _statusMessage = message;
        _statusIsError = isError;
        StateHasChanged();
    }

    private void ClearStatus()
    {
        _statusMessage = null;
        _statusIsError = false;
    }

    /// <summary>
    /// The window title (ADR 0117 §4), as VS Code builds it: the active tab's file, then the open
    /// folder, then the app's name (<c>*Program.cs — src — Code Editor</c>), with <c>*</c> while
    /// that file has unsaved changes.
    /// </summary>
    private string DocumentWindowTitle()
    {
        CodeEditorDocument? document = ActiveDocument;
        string? folder = _workspace?.Name;
        return (document, folder) switch
        {
            (null, null) => FormatWindowTitle(null),
            (null, _) => FormatWindowTitle(folder),
            (_, null) => FormatWindowTitle(document.Title, IsDirty(document)),
            _ => FormatWindowTitle($"{document.Title} — {folder}", IsDirty(document))
        };
    }

    /// <summary>The folder shown after a tab's name when another tab has the same name.</summary>
    private string? TabHint(CodeEditorDocument document)
    {
        if (document.Path is not { } path
            || _session.Documents.Count(other => other.Title == document.Title) < 2)
        {
            return null;
        }

        VirtualPath parent = CodeEditorWorkspace.ParentOf(path);
        return _workspace is { } workspace && workspace.Contains(parent) && parent != workspace.Root
            ? workspace.RelativePathOf(parent)
            : parent.Value;
    }

    /// <summary>The breadcrumbs above the editor: the path from the open folder, or from the root.</summary>
    private IEnumerable<string> Breadcrumbs(CodeEditorDocument document)
    {
        if (document.Path is not { } path)
        {
            return [document.Title];
        }

        if (_workspace is { } workspace && workspace.Contains(path))
        {
            return [workspace.Name, .. workspace.RelativePathOf(path).Split('/')];
        }

        return path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string SuggestedFileName(CodeEditorDocument document) =>
        document.SyntaxMode switch
        {
            CodeEditorSyntaxMode.CSharp => "script.cs",
            CodeEditorSyntaxMode.JavaScript => "script.js",
            CodeEditorSyntaxMode.TypeScript => "script.ts",
            CodeEditorSyntaxMode.Html => "index.html",
            CodeEditorSyntaxMode.Css => "styles.css",
            CodeEditorSyntaxMode.Json => "data.json",
            CodeEditorSyntaxMode.Markdown => "README.md",
            _ => "untitled.txt"
        };

    private string SyntaxModeLabel(CodeEditorSyntaxMode mode) => mode switch
    {
        CodeEditorSyntaxMode.PlainText => L["Syntax.PlainText"],
        CodeEditorSyntaxMode.CSharp => "C#",
        CodeEditorSyntaxMode.JavaScript => "JavaScript",
        CodeEditorSyntaxMode.TypeScript => "TypeScript",
        CodeEditorSyntaxMode.Html => "HTML",
        CodeEditorSyntaxMode.Css => "CSS",
        CodeEditorSyntaxMode.Json => "JSON",
        CodeEditorSyntaxMode.Markdown => "Markdown",
        _ => mode.ToString()
    };

    private static string ModeKey(CodeEditorSyntaxMode mode) => mode switch
    {
        CodeEditorSyntaxMode.CSharp => "cSharp",
        CodeEditorSyntaxMode.JavaScript => "javaScript",
        CodeEditorSyntaxMode.TypeScript => "typeScript",
        CodeEditorSyntaxMode.Html => "html",
        CodeEditorSyntaxMode.Css => "css",
        CodeEditorSyntaxMode.Json => "json",
        CodeEditorSyntaxMode.Markdown => "markdown",
        _ => "plainText"
    };

    /// <summary>The session's accessibility preferences changed (ADR 0105): Monaco takes them at once.</summary>
    private void OnAccessibilityChanged(object? sender, EventArgs args) => _ = InvokeAsync(async () =>
    {
        IJSObjectReference? editor = _editor;
        if (editor is null)
        {
            return;
        }

        try
        {
            HackAccessibilityPreferences preferences = await AppContext.Accessibility.GetPreferencesAsync(CancellationToken.None);
            await editor.InvokeVoidAsync("setAccessibility", new
            {
                highContrast = preferences.HighContrast,
                textScale = preferences.TextScale,
                reducedMotion = preferences.ReducedMotion
            });
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or ObjectDisposedException or TaskCanceledException)
        {
            // The editor went away meanwhile.
        }
    });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        AppContext.Accessibility.Changed -= OnAccessibilityChanged;
        _recoveryDebounce?.Cancel();
        _recoveryDebounce?.Dispose();
        if (!_recoveryClearedOnClose)
        {
            try
            {
                await PersistRecoveryAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Component disposal cannot surface a transient recovery failure.
            }
        }

        foreach (IJSObjectReference? reference in new[] { _workbench, _editor })
        {
            if (reference is null)
            {
                continue;
            }

            try
            {
                await reference.InvokeVoidAsync(reference == _editor ? "destroy" : "dispose");
                await reference.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }

        _dotNetReference?.Dispose();
    }
}
