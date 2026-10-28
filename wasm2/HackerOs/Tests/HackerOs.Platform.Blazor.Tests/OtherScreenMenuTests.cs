using HackerOs.Platform.Blazor.Display;
using HackerOs.Platform.Blazor.Windows;
using HackerOs.Platform.Core.Shell;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Blazor;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>
/// The « Autre écran » menus (ADR 0179 §3-5) and the move-to-screen shortcuts (ADR 0178 §6) of
/// <see cref="WindowKeyboardCommands"/>. The menus drawn in the browser are covered by the UI E2E tests.
/// </summary>
public sealed class OtherScreenMenuTests
{
    private static readonly WindowBounds Area = new(0, 0, 1280, 720);

    [Fact]
    public void The_maximize_button_menu_lists_snap_left_snap_right_and_full_screen()
    {
        (_, WindowKeyboardCommands commands, WindowId id) = Create(screens: 1);

        Assert.Equal(
            [WindowKeyboardAction.SnapLeft, WindowKeyboardAction.SnapRight, WindowKeyboardAction.ToggleFullScreen],
            commands.GetMenuItems(id, WindowMenuKind.MaximizeButton).Select(item => item.Action));
        Assert.Equal(commands.GetMenuItems(id), commands.GetMenuItems(id, WindowMenuKind.Window));
    }

    [Fact]
    public void Opening_a_menu_records_its_kind_and_whether_the_keyboard_opened_it()
    {
        (_, WindowKeyboardCommands commands, WindowId id) = Create(screens: 1);

        commands.OpenMenu(id, 40, 50, WindowMenuKind.MaximizeButton);
        Assert.Equal(WindowMenuKind.MaximizeButton, commands.Menu?.Kind);
        Assert.False(commands.Menu?.FromKeyboard);

        commands.OpenMenu(id, null, null);
        Assert.Equal(WindowMenuKind.Window, commands.Menu?.Kind);
        Assert.True(commands.Menu?.FromKeyboard);
    }

    [Fact]
    public void The_screen_submenu_lists_every_screen_and_disables_the_window_s_own()
    {
        (_, WindowKeyboardCommands commands, WindowId id) = Create(screens: 3);

        IReadOnlyList<WindowScreenMenuItem> items = commands.GetScreenMenuItems(id);

        Assert.Equal(["Screen 1", "Screen 2", "Screen 3"], items.Select(item => item.Label));
        Assert.Equal([false, true, true], items.Select(item => item.IsEnabled));
    }

    [Fact]
    public void With_one_screen_the_submenu_shows_screen_1_disabled()
    {
        (_, WindowKeyboardCommands commands, WindowId id) = Create(screens: 1);

        WindowScreenMenuItem only = Assert.Single(commands.GetScreenMenuItems(id));
        Assert.Equal(DisplayId.Primary, only.Display);
        Assert.False(only.IsEnabled);
    }

    [Fact]
    public void The_screen_submenu_speaks_the_session_language()
    {
        (WindowRuntime runtime, _, WindowId id) = Create(screens: 2);
        WindowKeyboardCommands french = new(runtime, locale: Localization.TestLocalization.CreateSessionLocale("fr_FR.UTF-8"));

        Assert.Equal(["Écran 1", "Écran 2"], french.GetScreenMenuItems(id).Select(item => item.Label));
    }

    [Fact]
    public void Choosing_a_screen_moves_the_window_there_and_keeps_it_focused()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands, WindowId id) = Create(screens: 2);
        commands.OpenMenu(id, 10, 10);

        Assert.True(commands.MoveToScreen(id, DisplayId.FromNumber(2)));

        Assert.Null(commands.Menu);
        WindowRuntimeState window = runtime.Windows.Single();
        Assert.Equal(DisplayId.FromNumber(2), window.DisplayId);
        Assert.True(window.IsFocused);
        Assert.False(commands.MoveToScreen(id, DisplayId.FromNumber(2)));
        Assert.False(commands.MoveToScreen(id, DisplayId.FromNumber(5)));
    }

    [Fact]
    public async Task The_shortcuts_move_the_active_window_to_the_next_and_previous_screen_going_round()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands, _) = Create(screens: 3);

        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.MoveToNextScreen));
        Assert.Equal(2, runtime.Windows.Single().DisplayId.Number);
        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.MoveToNextScreen));
        Assert.Equal(3, runtime.Windows.Single().DisplayId.Number);
        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.MoveToNextScreen));
        Assert.Equal(1, runtime.Windows.Single().DisplayId.Number);
        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.MoveToPreviousScreen));
        Assert.Equal(3, runtime.Windows.Single().DisplayId.Number);
    }

    [Fact]
    public async Task With_one_screen_the_shortcuts_do_nothing()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands, _) = Create(screens: 1);

        Assert.False(await commands.ExecuteAsync(WindowKeyboardAction.MoveToNextScreen));
        Assert.Equal(DisplayId.Primary, runtime.Windows.Single().DisplayId);
    }

    [Fact]
    public void The_shortcuts_have_linux_and_fallback_defaults()
    {
        Assert.Contains(WindowKeyBindingsSettingsDocuments.DefaultBindings, binding =>
            binding.Action == WindowKeyboardAction.MoveToNextScreen && binding.Set == WindowKeyBindingSet.Linux && binding.Chord == KeyChord.Parse("Super+Shift+Right"));
        Assert.Contains(WindowKeyBindingsSettingsDocuments.DefaultBindings, binding =>
            binding.Action == WindowKeyboardAction.MoveToPreviousScreen && binding.Set == WindowKeyBindingSet.Fallback && binding.Chord == KeyChord.Parse("Ctrl+Alt+Shift+Left"));
        Assert.Equal("move-to-next-screen", WindowKeyBindingsSettingsDocuments.KeyOf(WindowKeyboardAction.MoveToNextScreen));
    }

    [Fact]
    public async Task New_screen_opens_a_screen_then_moves_the_window_there()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands, WindowId id) = Create(screens: 1);
        Assert.False(commands.CanOpenScreen);
        commands.OpenScreen = () =>
        {
            DisplayId added = runtime.NextDisplayId;
            runtime.Apply(new AddDisplayCommand(added, Area));
            return Task.FromResult<DisplayId?>(added);
        };

        Assert.True(commands.CanOpenScreen);
        Assert.True(await commands.MoveToNewScreenAsync(id));

        Assert.Equal(DisplayId.FromNumber(2), runtime.Windows.Single().DisplayId);
    }

    [Fact]
    public async Task A_refused_new_screen_leaves_the_window_where_it_is()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands, WindowId id) = Create(screens: 1);
        commands.OpenScreen = () => Task.FromResult<DisplayId?>(null);

        Assert.False(await commands.MoveToNewScreenAsync(id));

        Assert.Equal(DisplayId.Primary, runtime.Windows.Single().DisplayId);
    }

    [Theory]
    [InlineData("window.screen.2", 2)]
    [InlineData("window.screen.9", 9)]
    [InlineData("window.screen.", null)]
    [InlineData("window.screen.-1", null)]
    [InlineData("window.new-screen", null)]
    [InlineData(null, null)]
    public void The_veil_reads_the_screen_number_of_the_highlighted_entry(string? itemId, int? expected) =>
        Assert.Equal(expected, ScreenIdentifyVeil.TargetOf(itemId));

    private static (WindowRuntime Runtime, WindowKeyboardCommands Commands, WindowId Id) Create(int screens)
    {
        WindowRuntime runtime = new(Area);
        for (int number = 2; number <= screens; number++)
        {
            runtime.Apply(new AddDisplayCommand(DisplayId.FromNumber(number), Area));
        }

        WindowRuntimeState state = new(
            WindowId.FromGuid(Guid.Parse("70000000-0000-0000-0000-000000000001")),
            "org.hackeros.test",
            WindowOwnerId.FromGuid(Guid.Parse("80000000-0000-0000-0000-000000000001")),
            "Test",
            null,
            new WindowBounds(40, 40, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240));
        runtime.Apply(new CreateWindowCommand(state));
        runtime.Apply(new FocusWindowCommand(state.Id));
        return (runtime, new WindowKeyboardCommands(runtime), state.Id);
    }
}
