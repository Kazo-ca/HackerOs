using HackerOs.AppSdk.Blazor;

namespace HackerOs.Apps.Browser;

/// <summary>
/// The mouse's Back and Forward buttons, Alt+←/→ and the phone's Back (ADR 0220 §4) reach the browser window
/// through the platform when the focus is on its own chrome; over the page, <c>PageBridge.js</c> relays them.
/// Both paths run the toolbar's Back and Forward.
/// </summary>
public sealed partial class BrowserWindow : IAppBackHandler, IAppForwardHandler
{
    private bool _lastCanNavigateBack;
    private bool _lastCanNavigateForward;

    /// <inheritdoc />
    public bool CanNavigateBack => CanGoBack;

    /// <inheritdoc />
    public bool CanNavigateForward => CanGoForward;

    /// <inheritdoc />
    public event Action? CanNavigateBackChanged;

    /// <inheritdoc />
    public event Action? CanNavigateForwardChanged;

    /// <inheritdoc />
    public async ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
    {
        if (!CanGoBack)
        {
            return AppBackResult.NotHandled;
        }

        await InvokeAsync(async () =>
        {
            await GoBack();
            StateHasChanged();
        });
        return AppBackResult.Handled;
    }

    /// <inheritdoc />
    public async ValueTask<AppBackResult> NavigateForwardAsync(AppForwardRequest request, CancellationToken cancellationToken = default)
    {
        if (!CanGoForward)
        {
            return AppBackResult.NotHandled;
        }

        await InvokeAsync(async () =>
        {
            await GoForward();
            StateHasChanged();
        });
        return AppBackResult.Handled;
    }

    /// <summary>Tells the shell when Back or Forward starts or stops having something to do; called after each render.</summary>
    private void RaiseNavigationAvailabilityChanged()
    {
        if (CanGoBack != _lastCanNavigateBack)
        {
            _lastCanNavigateBack = CanGoBack;
            CanNavigateBackChanged?.Invoke();
        }

        if (CanGoForward != _lastCanNavigateForward)
        {
            _lastCanNavigateForward = CanGoForward;
            CanNavigateForwardChanged?.Invoke();
        }
    }
}
