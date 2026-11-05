using Kazo.Os.Apps.FileExplorer.FileControl;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// Navigation history (back/forward/up) and the active view mode for File Explorer (`P2-FILE-002`).
/// Sorting, selection, and directory listing are no longer this class's concern — <see cref="FileView"/>
/// owns all three since the Phase 4 migration (`INT-001`); duplicating them here would be exactly what
/// `ADR 0037` commits to not doing. Only navigation history remains, since <see cref="FileView"/> itself
/// has no concept of "back"/"forward" — only the current directory and one-way <c>NavigateAsync</c>.
/// </summary>
public sealed class FileExplorerState
{
    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();

    /// <summary>Starts the history at <paramref name="initialPath"/>, normalized, with empty back and forward stacks.</summary>
    /// <param name="initialPath">The directory shown first; <c>/home/user</c> by default.</param>
    public FileExplorerState(string initialPath = "/home/user")
    {
        CurrentPath = NormalizePath(initialPath);
    }

    /// <summary>The normalized absolute path of the directory being shown.</summary>
    public string CurrentPath { get; private set; }

    /// <summary>True when there is a previously visited directory to go back to.</summary>
    public bool CanNavigateBack => _backStack.Count > 0;
    /// <summary>True after going back, until a new navigation clears the forward history.</summary>
    public bool CanNavigateForward => _forwardStack.Count > 0;
    /// <summary>True unless the current directory is the root <c>/</c>.</summary>
    public bool CanNavigateUp => CurrentPath != "/";

    /// <summary>The layout the embedded <see cref="FileView"/> uses (details by default).</summary>
    public FileViewMode ViewMode { get; set; } = FileViewMode.Details;
    /// <summary>The filter text typed in the search box; empty when no filter is applied.</summary>
    public string SearchQuery { get; set; } = string.Empty;

    /// <summary>Raised whenever <see cref="CurrentPath"/> changes, or when <see cref="NotifyStateChanged"/> is called.</summary>
    public event Action? StateChanged;

    /// <summary>
    /// Navigates to a directory like a browser link: the current path is pushed on the back stack and
    /// the forward history is discarded. Navigating to the directory already shown does nothing.
    /// </summary>
    /// <param name="newPath">The target path; <c>.</c> and <c>..</c> segments are resolved.</param>
    public void NavigateTo(string newPath)
    {
        string normalized = NormalizePath(newPath);
        if (normalized == CurrentPath)
        {
            return;
        }

        _backStack.Push(CurrentPath);
        _forwardStack.Clear();
        CurrentPath = normalized;
        NotifyStateChanged();
    }

    /// <summary>Returns to the previously visited directory, keeping the current one for <see cref="NavigateForward"/>.</summary>
    public void NavigateBack()
    {
        if (_backStack.Count == 0)
        {
            return;
        }

        _forwardStack.Push(CurrentPath);
        CurrentPath = _backStack.Pop();
        NotifyStateChanged();
    }

    /// <summary>Re-visits the directory left by the last <see cref="NavigateBack"/>.</summary>
    public void NavigateForward()
    {
        if (_forwardStack.Count == 0)
        {
            return;
        }

        _backStack.Push(CurrentPath);
        CurrentPath = _forwardStack.Pop();
        NotifyStateChanged();
    }

    /// <summary>Navigates to the parent directory, recorded in the back history like any navigation.</summary>
    public void NavigateUp()
    {
        if (!CanNavigateUp)
        {
            return;
        }

        int lastSlash = CurrentPath.LastIndexOf('/');
        string parentPath = lastSlash > 0 ? CurrentPath[..lastSlash] : "/";
        NavigateTo(parentPath);
    }

    /// <summary>Raises <see cref="StateChanged"/> so the window re-renders, e.g. after the view mode or search changed.</summary>
    public void NotifyStateChanged()
    {
        StateChanged?.Invoke();
    }

    private static string NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/";
        }

        string[] segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Stack<string> stack = new();

        foreach (string segment in segments)
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (stack.Count > 0) stack.Pop();
            }
            else
            {
                stack.Push(segment);
            }
        }

        return "/" + string.Join('/', stack.ToArray().Reverse());
    }
}
