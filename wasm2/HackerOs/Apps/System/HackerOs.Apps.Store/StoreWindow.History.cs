using HackerOs.AppSdk.Blazor;

namespace HackerOs.Apps.Store;

/// <summary>
/// The mouse's Back button, Alt+← and the phone's Back in the Hack Store (ADR 0220 §4): an app's page goes back
/// to the list it was opened from, a category back to the whole catalog.
/// </summary>
public partial class StoreWindow : IAppBackHandler
{
    private bool _lastCanNavigateBack;

    /// <inheritdoc />
    public bool CanNavigateBack => _view == StoreView.Detail || (_view == StoreView.Home && _categoryId is not null);

    /// <inheritdoc />
    public event Action? CanNavigateBackChanged;

    /// <inheritdoc />
    public async ValueTask<AppBackResult> NavigateBackAsync(AppBackRequest request, CancellationToken cancellationToken = default)
    {
        if (_view == StoreView.Detail)
        {
            CloseDetail();
        }
        else if (_view == StoreView.Home && _categoryId is not null)
        {
            _categoryId = null;
        }
        else
        {
            return AppBackResult.NotHandled;
        }

        await InvokeAsync(StateHasChanged);
        return AppBackResult.Handled;
    }

    /// <inheritdoc />
    protected override Task OnAppAfterRenderAsync(bool firstRender)
    {
        if (CanNavigateBack != _lastCanNavigateBack)
        {
            _lastCanNavigateBack = CanNavigateBack;
            CanNavigateBackChanged?.Invoke();
        }

        return base.OnAppAfterRenderAsync(firstRender);
    }
}
