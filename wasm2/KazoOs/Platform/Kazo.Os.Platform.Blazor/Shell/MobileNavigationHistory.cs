using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// Tracks the shell-level navigation history for the Mobile single-surface shell (plan
/// §7.3 step 3, <c>MOB-011</c>): an ordered record of which window was the primary surface before
/// a different one displaced it, so Back can return to it instead of only closing forward.
/// </summary>
/// <remarks>
/// Reacts to <see cref="WindowRuntime.StateChanged"/> and diffs the primary window
/// (<see cref="SingleSurfacePresentationPolicy.SelectPrimary"/>) rather than requiring every call
/// site that can change the primary (an app-dispatched intent launching a new window, Recent
/// activating a different one, anything else) to push explicitly. <see cref="TryPopPrevious"/>
/// marks the window it returns so the resulting Restore/Focus transition is recognized as a
/// return, not a new forward navigation, and is not immediately re-pushed.
/// </remarks>
public sealed class MobileNavigationHistory : IDisposable
{
    private readonly WindowRuntime _runtime;
    private readonly Stack<WindowId> _stack = new();
    private WindowId? _lastPrimary;
    private WindowId? _pendingReturn;

    /// <summary>Creates a history tracker over the Mobile shell's window runtime.</summary>
    public MobileNavigationHistory(WindowRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _lastPrimary = SingleSurfacePresentationPolicy.SelectPrimary(_runtime.Windows);
        _runtime.StateChanged += OnRuntimeStateChanged;
    }

    /// <summary>
    /// Pops the most recent still-open previous primary, discarding any stale entries for windows
    /// since closed. Returns <see langword="false"/> when no previous entry remains.
    /// </summary>
    public bool TryPopPrevious(out WindowId windowId)
    {
        while (_stack.TryPop(out WindowId candidate))
        {
            if (_runtime.Windows.Any(window => window.Id == candidate))
            {
                _pendingReturn = candidate;
                windowId = candidate;
                return true;
            }
        }

        windowId = default;
        return false;
    }

    private void OnRuntimeStateChanged()
    {
        WindowId? currentPrimary = SingleSurfacePresentationPolicy.SelectPrimary(_runtime.Windows);
        if (currentPrimary == _lastPrimary)
        {
            return;
        }

        bool isReturnToPending = _pendingReturn is not null && currentPrimary == _pendingReturn;
        if (!isReturnToPending
            && _lastPrimary is WindowId previous
            && _runtime.Windows.Any(window => window.Id == previous))
        {
            _stack.Push(previous);
        }

        _pendingReturn = null;
        _lastPrimary = currentPrimary;
    }

    /// <inheritdoc />
    public void Dispose() => _runtime.StateChanged -= OnRuntimeStateChanged;
}
