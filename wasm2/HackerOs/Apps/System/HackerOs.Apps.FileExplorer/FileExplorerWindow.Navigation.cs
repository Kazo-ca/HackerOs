using HackerOs.AppSdk.Blazor;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// The mouse's Back and Forward buttons, Alt+←/→ and the phone's Back (ADR 0220 §4): the platform sends them
/// to the focused window, and the explorer runs the same commands as its toolbar arrows.
/// </summary>
public partial class FileExplorerWindow : IAppBackHandler, IAppForwardHandler
{
    private bool _lastCanNavigateBack;
    private bool _lastCanNavigateForward;

    /// <inheritdoc />
    /// <remarks>Back leaves the trash first, then goes back through the folders visited.</remarks>
    public virtual bool CanNavigateBack => _showTrash || _state.CanNavigateBack;

    /// <inheritdoc />
    public bool CanNavigateForward => !_showTrash && _state.CanNavigateForward;

    /// <inheritdoc />
    public event Action? CanNavigateBackChanged;

    /// <inheritdoc />
    public event Action? CanNavigateForwardChanged;

    /// <inheritdoc />
    public virtual async ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
    {
        if (_showTrash)
        {
            _showTrash = false;
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
    public async ValueTask<AppBackResult> NavigateForwardAsync(AppForwardRequest request, CancellationToken cancellationToken = default)
    {
        if (!CanNavigateForward)
        {
            return AppBackResult.NotHandled;
        }

        await NavigateForwardAsync();
        await InvokeAsync(StateHasChanged);
        return AppBackResult.Handled;
    }

    /// <summary>Tells the shell when Back or Forward starts or stops having something to do; called after each render.</summary>
    private protected void RaiseNavigationAvailabilityChanged()
    {
        bool canNavigateBack = CanNavigateBack;
        if (canNavigateBack != _lastCanNavigateBack)
        {
            _lastCanNavigateBack = canNavigateBack;
            CanNavigateBackChanged?.Invoke();
        }

        bool canNavigateForward = CanNavigateForward;
        if (canNavigateForward != _lastCanNavigateForward)
        {
            _lastCanNavigateForward = canNavigateForward;
            CanNavigateForwardChanged?.Invoke();
        }
    }
}
