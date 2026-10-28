using HackerOs.Platform.Blazor.Shell;
using HackerOs.Platform.Core.Shell;
using HackerOs.Taskbar.Blazor;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Tests.Shell;

/// <summary>
/// ADR 0179 §1: one taskbar per screen. A secondary screen's taskbar lists its windows; screen 1's
/// lists every window or only its own (<c>primary-taskbar</c>); the Start menu shows on the screen of
/// the taskbar that opened it.
/// </summary>
public sealed class ScreenTaskbarTests
{
    private static readonly DisplayId Screen2 = DisplayId.FromNumber(2);
    private static readonly DisplayId Screen3 = DisplayId.FromNumber(3);

    [Theory]
    [InlineData(1, PrimaryTaskbarMode.All, 1, true)]
    [InlineData(1, PrimaryTaskbarMode.All, 2, true)]
    [InlineData(1, PrimaryTaskbarMode.Own, 1, true)]
    [InlineData(1, PrimaryTaskbarMode.Own, 2, false)]
    [InlineData(2, PrimaryTaskbarMode.All, 2, true)]
    [InlineData(2, PrimaryTaskbarMode.All, 1, false)]
    [InlineData(2, PrimaryTaskbarMode.Own, 3, false)]
    [InlineData(3, PrimaryTaskbarMode.Own, 3, true)]
    public void A_taskbar_lists_the_windows_its_screen_and_the_setting_allow(int taskbar, PrimaryTaskbarMode mode, int window, bool listed)
    {
        Assert.Equal(listed, TaskbarScreenFilter.Lists(DisplayId.FromNumber(taskbar), mode, DisplayId.FromNumber(window)));
    }

    [Fact]
    public void Each_taskbar_lists_its_windows_and_follows_a_window_that_changes_screen()
    {
        WindowRuntime runtime = TwoScreens();
        WindowRuntimeState first = Open(runtime, 1, DisplayId.Primary);
        WindowRuntimeState second = Open(runtime, 2, Screen2);
        PrimaryTaskbarMode mode = PrimaryTaskbarMode.All;
        using TaskbarWindowSourceAdapter primary = new(runtime, window => TaskbarScreenFilter.Lists(DisplayId.Primary, mode, window.DisplayId));
        using TaskbarWindowSourceAdapter secondary = new(runtime, window => TaskbarScreenFilter.Lists(Screen2, mode, window.DisplayId));

        Assert.Equal([first.Id, second.Id], primary.Windows.Select(item => item.Id));
        Assert.Equal([second.Id], secondary.Windows.Select(item => item.Id));

        int changes = 0;
        primary.Changed += () => changes++;
        mode = PrimaryTaskbarMode.Own;
        primary.Refresh();
        Assert.Equal(1, changes);
        Assert.Equal([first.Id], primary.Windows.Select(item => item.Id));

        runtime.Apply(new MoveWindowToDisplayCommand(first.Id, Screen2));
        Assert.Empty(primary.Windows);
        Assert.Equal(2, secondary.Windows.Count);
        Assert.Contains(secondary.Windows, item => item.Id == first.Id);
    }

    [Fact]
    public void The_unfiltered_adapter_still_lists_every_window()
    {
        WindowRuntime runtime = TwoScreens();
        Open(runtime, 1, DisplayId.Primary);
        Open(runtime, 2, Screen2);
        using TaskbarWindowSourceAdapter source = new(runtime);

        Assert.Equal(2, source.Windows.Count);
    }

    [Fact]
    public void The_start_menu_opens_on_the_screen_of_the_taskbar_clicked()
    {
        TaskbarLauncherAdapter launcher = new();
        ITaskbarLauncher primary = launcher.For(DisplayId.Primary);
        ITaskbarLauncher secondary = launcher.For(Screen2);
        int changes = 0;
        secondary.Changed += () => changes++;

        secondary.Toggle();
        Assert.True(launcher.IsOpen);
        Assert.Equal(Screen2, launcher.Display);
        Assert.True(secondary.IsOpen);
        Assert.False(primary.IsOpen);

        // The Start button of screen 1 moves the menu to screen 1 instead of closing it.
        primary.Toggle();
        Assert.True(primary.IsOpen);
        Assert.False(secondary.IsOpen);

        primary.Toggle();
        Assert.False(launcher.IsOpen);
        Assert.False(primary.IsOpen);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void The_launcher_without_a_screen_keeps_opening_on_screen_1()
    {
        TaskbarLauncherAdapter launcher = new();

        launcher.Toggle();
        Assert.True(launcher.IsOpen);
        Assert.Equal(DisplayId.Primary, launcher.Display);

        launcher.For(Screen3).Toggle();
        launcher.Open();
        Assert.Equal(Screen3, launcher.Display);

        launcher.Close();
        Assert.False(launcher.For(Screen3).IsOpen);
    }

    private static WindowRuntime TwoScreens()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        runtime.Apply(new AddDisplayCommand(Screen2, new WindowBounds(0, 0, 1024, 768)));
        return runtime;
    }

    private static WindowRuntimeState Open(WindowRuntime runtime, int seed, DisplayId display)
    {
        WindowRuntimeState state = new(
            WindowId.FromGuid(Guid.Parse($"10000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"20000000-0000-0000-0000-{seed:D12}")),
            $"Test App {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 1200, 900),
            WindowModality.Modeless,
            null)
        {
            DisplayId = display,
        };
        runtime.Apply(new CreateWindowCommand(state));
        return state;
    }
}
