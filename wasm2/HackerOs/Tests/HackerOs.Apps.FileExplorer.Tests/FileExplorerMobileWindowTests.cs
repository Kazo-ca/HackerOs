using Xunit;
using HackerOs.AppSdk.Blazor;

namespace HackerOs.Apps.FileExplorer.Tests;

/// <summary>ADR 0171 §1: the File Explorer declares its own window for phones.</summary>
public sealed class FileExplorerMobileWindowTests
{
    [Fact]
    public void The_mobile_entry_point_is_the_phone_window()
    {
        FileExplorerApp app = new();

        Assert.Equal(typeof(FileExplorerWindow), app.EntryPoint);
        Assert.Equal(typeof(FileExplorerMobileWindow), app.MobileEntryPoint);
    }

    [Fact]
    public void The_phone_window_shares_the_explorer_and_answers_the_system_Back_button()
    {
        Assert.True(typeof(FileExplorerWindow).IsAssignableFrom(typeof(FileExplorerMobileWindow)));
        Assert.True(typeof(IAppBackHandler).IsAssignableFrom(typeof(FileExplorerMobileWindow)));
    }

    [Fact]
    public void Both_explorer_windows_answer_the_mouse_Back_and_Forward_buttons()
    {
        // ADR 0220 §4: the platform sends the mouse buttons, Alt+arrows and the phone's Back to these.
        Assert.True(typeof(IAppBackHandler).IsAssignableFrom(typeof(FileExplorerWindow)));
        Assert.True(typeof(IAppForwardHandler).IsAssignableFrom(typeof(FileExplorerWindow)));
        Assert.True(typeof(IAppForwardHandler).IsAssignableFrom(typeof(FileExplorerMobileWindow)));
    }
}
