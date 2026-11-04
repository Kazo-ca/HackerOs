using Kazo.Os.App.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>Where a search result asks the window to go: a file and the match to select.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Length">Characters to select.</param>
public sealed record CodeEditorNavigation(VirtualPath Path, int Line, int Column, int Length);

/// <summary>
/// Code-behind of the Search view. Typing searches after a short pause (VS Code's
/// <c>search.searchOnType</c>), Enter searches at once; a search still running when the query
/// changes is cancelled, so results never come back out of order.
/// </summary>
public partial class CodeEditorSearchView
{
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The Code Editor's texts in its own process's language (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Pause after the last keystroke before searching.</summary>
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(300);

    private ElementReference _searchInput;
    private string _text = string.Empty;
    private string _include = string.Empty;
    private string _exclude = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private bool _useRegex;
    private bool _showDetails;
    private bool _searching;
    private bool _focusPending;
    private CodeEditorSearchResult _result = CodeEditorSearchResult.Empty;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pending;
    private CodeEditorWorkspace? _searchedWorkspace;

    /// <summary>Gets or sets the open folder, or null when none is open.</summary>
    [Parameter]
    public CodeEditorWorkspace? Workspace { get; set; }

    /// <summary>Gets or sets the content of an open tab for a path (searched instead of the saved file), or null.</summary>
    [Parameter]
    public Func<VirtualPath, string?>? OpenContent { get; set; }

    /// <summary>Gets or sets a step run before each search (the window brings the tabs' latest text from the editor).</summary>
    [Parameter]
    public Func<Task>? BeforeSearch { get; set; }

    /// <summary>Gets or sets the callback that opens a file at a match.</summary>
    [Parameter]
    public EventCallback<CodeEditorNavigation> OnOpenMatch { get; set; }

    /// <summary>Gets or sets the callback that asks the window for its Open Folder dialog.</summary>
    [Parameter]
    public EventCallback OnOpenFolder { get; set; }

    /// <summary>
    /// Focuses the search box, optionally seeding it (the editor's selection, as VS Code does) and
    /// the "files to include" box (the explorer's "Find in Folder...").
    /// </summary>
    public async Task FocusAsync(string? text = null, string? include = null)
    {
        bool changed = false;
        if (!string.IsNullOrEmpty(text) && !text.Contains('\n'))
        {
            changed |= _text != text;
            _text = text;
        }

        if (include is not null)
        {
            changed |= _include != include;
            _include = include;
            _showDetails |= include.Length > 0;
        }

        _focusPending = true;
        StateHasChanged();
        if (changed)
        {
            await RunNowAsync();
        }
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_searchedWorkspace, Workspace))
        {
            // Another folder: the old results point at files that are not in it.
            _searchedWorkspace = Workspace;
            Cancel();
            _result = CodeEditorSearchResult.Empty;
            _collapsed.Clear();
            if (Workspace is not null && _text.Length > 0)
            {
                _ = InvokeAsync(RunNowAsync);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusPending && Workspace is not null)
        {
            _focusPending = false;
            try
            {
                await _searchInput.FocusAsync();
            }
            catch (Exception)
            {
                // Not rendered (headless test DOM).
            }
        }
    }

    private void OnTextInput(ChangeEventArgs args)
    {
        _text = args.Value?.ToString() ?? string.Empty;
        Schedule();
    }

    private void OnIncludeInput(ChangeEventArgs args)
    {
        _include = args.Value?.ToString() ?? string.Empty;
        Schedule();
    }

    private void OnExcludeInput(ChangeEventArgs args)
    {
        _exclude = args.Value?.ToString() ?? string.Empty;
        Schedule();
    }

    private async Task OnSearchKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Enter")
        {
            await RunNowAsync();
        }
        else if (args.Key == "Escape")
        {
            Clear();
        }
    }

    private void ToggleMatchCase()
    {
        _matchCase = !_matchCase;
        Schedule(immediate: true);
    }

    private void ToggleWholeWord()
    {
        _wholeWord = !_wholeWord;
        Schedule(immediate: true);
    }

    private void ToggleRegex()
    {
        _useRegex = !_useRegex;
        Schedule(immediate: true);
    }

    private void ToggleDetails() => _showDetails = !_showDetails;

    private void ToggleFile(string relativePath)
    {
        if (!_collapsed.Remove(relativePath))
        {
            _collapsed.Add(relativePath);
        }
    }

    private void CollapseAll()
    {
        foreach (CodeEditorSearchFileResult file in _result.Files)
        {
            _collapsed.Add(file.File.RelativePath);
        }
    }

    private void Clear()
    {
        Cancel();
        _text = string.Empty;
        _result = CodeEditorSearchResult.Empty;
        _collapsed.Clear();
        _searching = false;
    }

    private Task RunNowAsync()
    {
        Schedule(immediate: true);
        return Task.CompletedTask;
    }

    /// <summary>Starts a search after <see cref="TypingDelay"/> (or now), replacing any pending one.</summary>
    private void Schedule(bool immediate = false)
    {
        Cancel();
        if (Workspace is not { } workspace || _text.Length == 0)
        {
            _result = CodeEditorSearchResult.Empty;
            _searching = false;
            return;
        }

        CancellationTokenSource pending = new();
        _pending = pending;
        CodeEditorSearchQuery query = new(_text, _matchCase, _wholeWord, _useRegex, _include, _exclude);
        _ = RunAsync(workspace, query, immediate ? TimeSpan.Zero : TypingDelay, pending.Token);
    }

    private async Task RunAsync(CodeEditorWorkspace workspace, CodeEditorSearchQuery query, TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            _searching = true;
            await InvokeAsync(StateHasChanged);
            if (BeforeSearch is not null)
            {
                await BeforeSearch();
            }

            // ADR 0128/0140: read and match in a background task, so a large folder never freezes the desktop.
            CodeEditorSearchResult result = await new CodeEditorWorkspaceSearch(workspace)
                .SearchInBackgroundAsync(query, OpenContent, progress: null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _result = result;
            _collapsed.Clear();
            _searching = false;
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer query.
        }
    }

    private Task OpenMatchAsync(CodeEditorWorkspaceFile file, CodeEditorSearchMatch match) =>
        OnOpenMatch.InvokeAsync(new CodeEditorNavigation(file.Path, match.Line, match.Column, match.Length));

    private string Summary()
    {
        if (_searching)
        {
            return L["Search.Searching"];
        }

        if (_text.Length == 0 || _result.Error is not null)
        {
            return string.Empty;
        }

        if (_result.MatchCount == 0)
        {
            return Strings.Count(L, "Search.NoResults", _result.FilesSearched);
        }

        string summary = L["Search.Summary", Strings.Count(L, "Search.ResultCount", _result.MatchCount), Strings.Count(L, "Search.FileCount", _result.Files.Count)];
        return _result.Truncated ? L["Search.Truncated", summary] : summary;
    }

    private static string Before(CodeEditorSearchMatch match) =>
        match.LineText[..Math.Clamp(match.PreviewOffset, 0, match.LineText.Length)];

    private static string Matched(CodeEditorSearchMatch match)
    {
        int start = Math.Clamp(match.PreviewOffset, 0, match.LineText.Length);
        return match.LineText.Substring(start, Math.Clamp(match.Length, 0, match.LineText.Length - start));
    }

    private static string After(CodeEditorSearchMatch match)
    {
        int start = Math.Clamp(match.PreviewOffset, 0, match.LineText.Length);
        int end = start + Math.Clamp(match.Length, 0, match.LineText.Length - start);
        return match.LineText[end..];
    }

    private void Cancel()
    {
        // Cancelled, not disposed: the superseded search may still be reading its token.
        _pending?.Cancel();
        _pending = null;
    }

    /// <inheritdoc />
    public void Dispose() => Cancel();
}
