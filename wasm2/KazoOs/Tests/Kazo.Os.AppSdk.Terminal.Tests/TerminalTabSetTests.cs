using Kazo.Os.AppSdk.Terminal;
using Xunit;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>Ordering rules of the console's tabs (ADR 0058, section 4), as gnome-terminal.</summary>
public sealed class TerminalTabSetTests
{
    [Fact]
    public void A_new_tab_opens_after_the_active_one_and_becomes_active()
    {
        TerminalTabSet tabs = new();
        TerminalTabState first = tabs.Open(null);
        TerminalTabState third = tabs.Open("/tmp");
        tabs.Activate(first);

        TerminalTabState second = tabs.Open("/home/alice/docs");

        Assert.Equal([first, second, third], tabs.Tabs);
        Assert.Same(second, tabs.Active);
        Assert.Equal("/home/alice/docs", second.InitialCwd);
        Assert.Equal(3, tabs.Tabs.Select(static tab => tab.Id).Distinct().Count());
    }

    [Fact]
    public void Closing_the_active_tab_activates_its_right_neighbour_else_its_left_one()
    {
        TerminalTabSet tabs = new();
        TerminalTabState a = tabs.Open(null);
        TerminalTabState b = tabs.Open(null);
        TerminalTabState c = tabs.Open(null);
        tabs.Activate(b);

        Assert.True(tabs.Close(b));
        Assert.Same(c, tabs.Active);

        Assert.True(tabs.Close(c));
        Assert.Same(a, tabs.Active);
    }

    [Fact]
    public void Closing_an_inactive_tab_keeps_the_active_one()
    {
        TerminalTabSet tabs = new();
        TerminalTabState a = tabs.Open(null);
        TerminalTabState b = tabs.Open(null);

        Assert.True(tabs.Close(a));
        Assert.Same(b, tabs.Active);
        Assert.False(tabs.Close(a));
    }

    [Fact]
    public void Closing_the_last_tab_leaves_no_active_tab()
    {
        TerminalTabSet tabs = new();
        TerminalTabState only = tabs.Open(null);

        Assert.True(tabs.Close(only));

        Assert.Empty(tabs.Tabs);
        Assert.Null(tabs.Active);
    }

    [Fact]
    public void Switching_wraps_around_in_both_directions()
    {
        TerminalTabSet tabs = new();
        TerminalTabState a = tabs.Open(null);
        TerminalTabState b = tabs.Open(null);
        TerminalTabState c = tabs.Open(null);

        Assert.True(tabs.Switch(1));
        Assert.Same(a, tabs.Active);
        Assert.True(tabs.Switch(-1));
        Assert.Same(c, tabs.Active);
        Assert.True(tabs.Switch(-1));
        Assert.Same(b, tabs.Active);
    }

    [Fact]
    public void Switching_with_one_tab_does_nothing()
    {
        TerminalTabSet tabs = new();
        TerminalTabState only = tabs.Open(null);

        Assert.False(tabs.Switch(1));
        Assert.Same(only, tabs.Active);
    }
}
