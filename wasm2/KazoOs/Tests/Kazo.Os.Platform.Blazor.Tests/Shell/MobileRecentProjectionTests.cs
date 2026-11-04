using Kazo.Os.MobileShell.Blazor;
using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>Tests for the <c>MOB-011</c> Recent-surface projection.</summary>
public sealed class MobileRecentProjectionTests
{
    [Fact]
    public void BuildItems_orders_by_z_order_descending()
    {
        WindowRuntimeState low = CreateState(1, zOrder: 1);
        WindowRuntimeState high = CreateState(2, zOrder: 5);
        WindowRuntimeState middle = CreateState(3, zOrder: 3);

        IReadOnlyList<MobileRecentItem> items = MobileRecentProjection.BuildItems([low, high, middle]);

        Assert.Equal([high.Id, middle.Id, low.Id], items.Select(item => item.Id));
    }

    [Fact]
    public void BuildItems_maps_title_icon_and_focus_state()
    {
        WindowRuntimeState window = CreateState(1, zOrder: 1, title: "Terminal", iconAssetPath: "/icons/terminal.svg", isFocused: true);

        MobileRecentItem item = Assert.Single(MobileRecentProjection.BuildItems([window]));

        Assert.Equal(window.Id, item.Id);
        Assert.Equal("Terminal", item.Title);
        Assert.Equal("/icons/terminal.svg", item.IconAssetPath);
        Assert.True(item.IsFocused);
    }

    [Fact]
    public void BuildItems_returns_empty_for_no_windows()
    {
        Assert.Empty(MobileRecentProjection.BuildItems([]));
    }

    private static WindowRuntimeState CreateState(
        int seed,
        int zOrder,
        string? title = null,
        string? iconAssetPath = null,
        bool isFocused = false) =>
        new(
            WindowId.FromGuid(Guid.Parse($"90000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"a0000000-0000-0000-0000-{seed:D12}")),
            title ?? $"Test {seed}",
            iconAssetPath,
            new WindowBounds(0, 0, 320, 480),
            null,
            zOrder,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            WindowModality.Modeless,
            null,
            isFocused: isFocused);
}
