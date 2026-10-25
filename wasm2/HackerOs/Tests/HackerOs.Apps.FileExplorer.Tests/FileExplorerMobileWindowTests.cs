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
}
