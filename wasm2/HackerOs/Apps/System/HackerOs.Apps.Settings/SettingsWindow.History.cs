using HackerOs.AppSdk.Blazor;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Back and Forward through the pages of Settings (ADR 0220 §4): the mouse's Back and Forward buttons, Alt+←/→
/// and the phone's Back. On a phone, Back closes the open menu first.
/// </summary>
public partial class SettingsWindow : IAppBackHandler, IAppForwardHandler
{
    private readonly SettingsPageHistory _pageHistory = new();
    private bool _lastCanNavigateBack;
    private bool _lastCanNavigateForward;

    /// <inheritdoc />
    public bool CanNavigateBack => _navOverlayOpen || _pageHistory.PeekBack(_activePage, IsVisiblePage) is not null;

    /// <inheritdoc />
    public bool CanNavigateForward => _pageHistory.PeekForward(_activePage, IsVisiblePage) is not null;

    /// <inheritdoc />
    public event Action? CanNavigateBackChanged;

    /// <inheritdoc />
    public event Action? CanNavigateForwardChanged;

    /// <inheritdoc />
    public async ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
    {
        if (_navOverlayOpen)
        {
            _navOverlayOpen = false;
        }
        else if (_pageHistory.Back(_activePage, IsVisiblePage) is { } previous)
        {
            _activePage = previous;
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
        if (_pageHistory.Forward(_activePage, IsVisiblePage) is not { } next)
        {
            return AppBackResult.NotHandled;
        }

        _activePage = next;
        _navOverlayOpen = false;
        await InvokeAsync(StateHasChanged);
        return AppBackResult.Handled;
    }

    private bool IsVisiblePage(string pageId) => VisiblePages.Any(page => page.Id == pageId);

    /// <summary>Tells the shell when Back or Forward starts or stops having something to do; called after each render.</summary>
    private void RaiseNavigationAvailabilityChanged()
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
