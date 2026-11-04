using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>Code-behind of the Explorer view: selection, inline creation, file menu entries, rename tracking.</summary>
public partial class CodeEditorExplorer
{
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The Code Editor's texts in its own process's language (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private IHackFileView? _files;
    private ElementReference _createInput;
    private CreationKind? _creating;
    private string _createName = string.Empty;
    private VirtualPath _createDirectory;
    private string? _createError;
    private bool _focusCreateInput;
    private bool _committing;

    private enum CreationKind
    {
        File,
        Folder
    }

    /// <summary>Gets or sets the open folder, or null when none is open.</summary>
    [Parameter]
    public CodeEditorWorkspace? Workspace { get; set; }

    /// <summary>Gets or sets the callback that opens a file in a tab and focuses it (double click, Enter, after creating it).</summary>
    [Parameter]
    public EventCallback<VirtualPath> OnOpenFile { get; set; }

    /// <summary>
    /// Gets or sets the callback that shows a file without taking the focus (single click), so the
    /// tree keeps the keyboard (arrows, Delete, F2), as in VS Code.
    /// </summary>
    [Parameter]
    public EventCallback<VirtualPath> OnPreviewFile { get; set; }

    /// <summary>Gets or sets the callback that asks the window for its Open Folder dialog.</summary>
    [Parameter]
    public EventCallback OnOpenFolder { get; set; }

    /// <summary>Gets or sets the callback of the folder menu's "Find in Folder..." entry.</summary>
    [Parameter]
    public EventCallback<VirtualPath> OnFindInFolder { get; set; }

    /// <summary>Gets or sets the callback that opens the integrated terminal in a folder.</summary>
    [Parameter]
    public EventCallback<VirtualPath> OnOpenInTerminal { get; set; }

    /// <summary>Gets or sets the callback told that an entry was renamed (old path, new path), so open tabs follow it.</summary>
    [Parameter]
    public EventCallback<(VirtualPath OldPath, VirtualPath NewPath)> OnRenamed { get; set; }

    /// <summary>Gets or sets the callback receiving the tree's errors (a delete or a copy that failed), for the status bar.</summary>
    [Parameter]
    public EventCallback<string> OnError { get; set; }

    /// <summary>Starts creating a file in the selected folder (the window's "New File" command when a folder is open).</summary>
    public void BeginNewFile() => BeginCreate(CreationKind.File, null);

    /// <summary>Re-reads the tree from the file system.</summary>
    public async Task RefreshAsync()
    {
        if (_files is not null)
        {
            await _files.RefreshAsync();
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusCreateInput && _creating is not null)
        {
            _focusCreateInput = false;
            try
            {
                await _createInput.FocusAsync();
            }
            catch (Exception)
            {
                // Not rendered (headless test DOM).
            }
        }
    }

    private void OnControllerChanged(IHackFileView? files) => _files = files;

    /// <summary>VS Code previews a file on a single click; folders just expand.</summary>
    private Task OnSelectionChanged(IReadOnlyList<HackFileViewItem> selection) =>
        selection is [{ IsDirectory: false } file] ? OnPreviewFile.InvokeAsync(VirtualPath.Parse(file.Path)) : Task.CompletedTask;

    /// <summary>A file activated (double click, Enter) opens in a tab, not in its default app.</summary>
    private async Task OnOpening(HackFileViewOpeningEventArgs e)
    {
        if (!e.Item.IsDirectory)
        {
            e.Handled = true;
            await OnOpenFile.InvokeAsync(VirtualPath.Parse(e.Item.Path));
        }
    }

    private Task OnRenamedItem(HackFileViewRenamedEventArgs e) =>
        OnRenamed.InvokeAsync((VirtualPath.Parse(e.OldPath), VirtualPath.Parse(e.NewPath)));

    /// <summary>
    /// The tree's file menus (plan §7) with VS Code's entries: on the background, "New File" and
    /// "New Folder" with the name typed first replace the control's "New folder"; on a folder, the same
    /// pair, "Find in Folder..." and "Open in Integrated Terminal".
    /// </summary>
    private void OnContextMenuOpening(HackFileMenuOpeningEventArgs e)
    {
        switch (e.Scope)
        {
            case FileMenuScope.Background:
                e.Menu.Hide(HackFileMenuItems.NewFolder);
                AddGroup(e.Menu,
                    Item("explorer-new-file", L["Explorer.NewFile.Title"], "file-plus", () => BeginCreateAsync(CreationKind.File, Workspace?.Root)),
                    Item("explorer-new-folder", L["Explorer.NewFolder.Title"], "folder-plus", () => BeginCreateAsync(CreationKind.Folder, Workspace?.Root)));
                break;
            case FileMenuScope.Directory when e.Items is [{ } folder]:
                VirtualPath path = VirtualPath.Parse(folder.Path);
                AddGroup(e.Menu,
                    Item("explorer-new-file", L["Explorer.NewFile.Title"], "file-plus", () => BeginCreateAsync(CreationKind.File, path)),
                    Item("explorer-new-folder", L["Explorer.NewFolder.Title"], "folder-plus", () => BeginCreateAsync(CreationKind.Folder, path)),
                    Item("explorer-find-in-folder", L["Explorer.FindInFolder"], "search", () => OnFindInFolder.InvokeAsync(path)),
                    Item("explorer-open-in-terminal", L["Explorer.OpenInTerminal"], "terminal", () => OnOpenInTerminal.InvokeAsync(path)));
                break;
        }
    }

    /// <summary>Adds the editor's entries as one group, right after the control's first group.</summary>
    private static void AddGroup(HackMenuDefinition menu, params HackMenuItemDefinition[] items)
    {
        HackMenuGroupDefinition group = new();
        foreach (HackMenuItemDefinition item in items)
        {
            group.Items.Add(item);
        }

        menu.Groups.Insert(Math.Min(1, menu.Groups.Count), group);
    }

    private Task BeginCreateAsync(CreationKind kind, VirtualPath? directory)
    {
        BeginCreate(kind, directory);
        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Shows the name box. The target is the given folder, else the selected folder, else the
    /// selected file's folder, else the root, as VS Code picks it.
    /// </summary>
    private void BeginCreate(CreationKind kind, VirtualPath? directory)
    {
        if (Workspace is null)
        {
            return;
        }

        VirtualPath target = directory ?? Workspace.Root;
        if (directory is null && _files?.SelectedItems is [{ } selected] && Workspace.Contains(VirtualPath.Parse(selected.Path)))
        {
            VirtualPath selectedPath = VirtualPath.Parse(selected.Path);
            target = selected.IsDirectory ? selectedPath : CodeEditorWorkspace.ParentOf(selectedPath);
        }

        _creating = kind;
        _createDirectory = target;
        _createName = string.Empty;
        _createError = null;
        _focusCreateInput = true;
    }

    private void OnCreateKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            CancelCreate();
        }
        else if (args.Key != "Enter")
        {
            _createError = null;
        }
    }

    /// <summary>
    /// Enter creates on key-up: a new file opens with the editor focused, and on key-down the rest
    /// of the keystroke would land in it as a new line.
    /// </summary>
    private async Task OnCreateKeyUpAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await CommitCreateAsync();
        }
    }

    /// <summary>Leaving the box creates the entry when a name is typed, as VS Code does; an empty box is dropped.</summary>
    private async Task OnCreateBlurAsync()
    {
        if (string.IsNullOrWhiteSpace(_createName))
        {
            CancelCreate();
            return;
        }

        await CommitCreateAsync();
    }

    private async Task CommitCreateAsync()
    {
        if (_creating is not { } kind || Workspace is null || _committing)
        {
            return;
        }

        if (CodeEditorWorkspace.ValidateName(_createName) is { } invalid)
        {
            _createError = invalid;
            return;
        }

        _committing = true;
        try
        {
            CodeEditorCreateResult result = kind == CreationKind.File
                ? await Workspace.CreateFileAsync(_createDirectory, _createName)
                : await Workspace.CreateDirectoryAsync(_createDirectory, _createName);
            if (!result.Succeeded || result.Path is not { } created)
            {
                _createError = result.Error;
                return;
            }

            CancelCreate();
            await RefreshAsync();
            if (kind == CreationKind.File)
            {
                await OnOpenFile.InvokeAsync(created);
            }
        }
        finally
        {
            _committing = false;
        }
    }

    private void CancelCreate()
    {
        _creating = null;
        _createName = string.Empty;
        _createError = null;
    }

    /// <summary>One entry of the editor's own, run in the editor's context (the component that owns it).</summary>
    private HackMenuItemDefinition Item(string id, string label, string icon, Func<Task> onActivate) =>
        new(id, label)
        {
            Icon = HackIcons.Vector(IconLibrary.Lucide, icon),
            OnClick = EventCallback.Factory.Create<object?>(this, _ => onActivate())
        };
}
