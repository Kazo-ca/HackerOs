using Xunit;
using Kazo.Os.AppSdk.Blazor;

namespace Kazo.Os.Apps.Store.Tests;

/// <summary>ADR 0220 §4: the mouse's Back button and the phone's Back in the Kazo OS Store.</summary>
public sealed class StoreHardwareNavigationTests
{
    [Fact]
    public async Task Back_closes_an_apps_page_and_has_nothing_to_do_on_the_catalog()
    {
        StoreWindow store = new();
        Assert.False(store.CanNavigateBack);
        AppBackResult onCatalog = await store.NavigateBackAsync(new AppBackRequest(AppBackSource.MouseButton));
        Assert.Equal(AppBackResultStatus.NotHandled, onCatalog.Status);
    }
}
