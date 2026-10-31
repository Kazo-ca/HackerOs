using Xunit;
using HackerOs.AppSdk.Blazor;

namespace HackerOs.Apps.Settings.Tests;

/// <summary>ADR 0220 §4: Back and Forward through the pages of Settings.</summary>
public sealed class SettingsPageHistoryTests
{
    private static bool Always(string _) => true;

    [Fact]
    public void Settings_answers_the_mouse_Back_and_Forward_buttons()
    {
        Assert.True(typeof(IAppBackHandler).IsAssignableFrom(typeof(SettingsWindow)));
        Assert.True(typeof(IAppForwardHandler).IsAssignableFrom(typeof(SettingsWindow)));
    }

    [Fact]
    public void Back_and_forward_walk_through_the_pages_chosen()
    {
        SettingsPageHistory history = new();
        history.Visit("appearance", "sound");
        history.Visit("sound", "about");

        Assert.Equal("sound", history.Back("about", Always));
        Assert.Equal("appearance", history.Back("sound", Always));
        Assert.Null(history.Back("appearance", Always));
        Assert.Equal("sound", history.Forward("appearance", Always));
        Assert.Equal("about", history.Forward("sound", Always));
        Assert.Null(history.Forward("about", Always));
    }

    [Fact]
    public void Choosing_a_page_clears_forward_and_choosing_the_same_page_records_nothing()
    {
        SettingsPageHistory history = new();
        history.Visit("appearance", "sound");
        Assert.Equal("appearance", history.Back("sound", Always));

        history.Visit("appearance", "users");
        history.Visit("users", "users");

        Assert.Null(history.PeekForward("users", Always));
        Assert.Equal("appearance", history.PeekBack("users", Always));
    }

    [Fact]
    public void Pages_that_left_the_menu_are_skipped()
    {
        SettingsPageHistory history = new();
        history.Visit("appearance", "backup");
        history.Visit("backup", "about");

        Assert.Equal("appearance", history.Back("about", page => page != "backup"));
    }
}
