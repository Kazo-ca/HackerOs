using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>
/// Code-behind of the quick-open box. The folder is listed once when the box opens (the listing
/// walks the whole folder), then each keystroke only re-ranks that list.
/// </summary>
public partial class CodeEditorQuickOpenPalette
{
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The Code Editor's texts in its own process's language (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    private ElementReference _input;
    private bool _open;
    private bool _loading;
    private bool _focusPending;
    private string _text = string.Empty;
    private int _selected;
    private IReadOnlyList<CodeEditorWorkspaceFile> _files = [];
    private IReadOnlyList<CodeEditorWorkspaceFile> _matches = [];
    private CancellationTokenSource? _listing;

    /// <summary>Gets or sets the open folder whose files are offered.</summary>
    [Parameter]
    public CodeEditorWorkspace? Workspace { get; set; }

    /// <summary>Gets or sets the callback that opens the chosen file.</summary>
    [Parameter]
    public EventCallback<CodeEditorWorkspaceFile> OnPick { get; set; }

    /// <summary>Gets or sets the callback told that the box closed, so the window can refocus the editor.</summary>
    [Parameter]
    public EventCallback OnClosed { get; set; }

    /// <summary>Gets whether the box is shown.</summary>
    public bool IsOpen => _open;

    /// <summary>Shows the box and lists the folder's files.</summary>
    public async Task OpenAsync()
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        _listing?.Cancel();
        CancellationTokenSource listing = new();
        _listing = listing;
        _open = true;
        _loading = true;
        _text = string.Empty;
        _selected = 0;
        _matches = [];
        _focusPending = true;
        StateHasChanged();
        try
        {
            CodeEditorWorkspaceListing files = await workspace.ListFilesAsync(listing.Token);
            _files = files.Files;
            _loading = false;
            Filter();
            StateHasChanged();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Hides the box.</summary>
    public async Task CloseAsync()
    {
        if (!_open)
        {
            return;
        }

        _listing?.Cancel();
        _open = false;
        _files = [];
        _matches = [];
        await OnClosed.InvokeAsync();
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusPending && _open)
        {
            _focusPending = false;
            try
            {
                await _input.FocusAsync();
            }
            catch (Exception)
            {
                // Not rendered (headless test DOM).
            }
        }
    }

    private void OnInput(ChangeEventArgs args)
    {
        _text = args.Value?.ToString() ?? string.Empty;
        Filter();
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        switch (args.Key)
        {
            case "ArrowDown":
                _selected = _matches.Count == 0 ? 0 : (_selected + 1) % _matches.Count;
                break;
            case "ArrowUp":
                _selected = _matches.Count == 0 ? 0 : (_selected - 1 + _matches.Count) % _matches.Count;
                break;
            case "Escape":
                await CloseAsync();
                break;
        }
    }

    /// <summary>
    /// Enter opens on key-up: the editor takes the focus when the box closes, and on key-down the
    /// rest of the keystroke (its input event) would land in the file as a new line.
    /// </summary>
    private async Task OnKeyUpAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && _selected < _matches.Count)
        {
            await PickAsync(_matches[_selected]);
        }
    }

    private async Task PickAsync(CodeEditorWorkspaceFile file)
    {
        await CloseAsync();
        await OnPick.InvokeAsync(file);
    }

    private void Filter()
    {
        _matches = CodeEditorQuickOpen.Filter(_files, _text);
        _selected = 0;
    }

    /// <inheritdoc />
    public void Dispose() => _listing?.Cancel();
}
