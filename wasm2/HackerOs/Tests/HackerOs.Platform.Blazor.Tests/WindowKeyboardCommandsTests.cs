using HackerOs.Platform.Blazor.Windows;
using HackerOs.Platform.Core.Shell;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>ADR 0095 §3: the window manager's keyboard commands.</summary>
public sealed class WindowKeyboardCommandsTests
{
    private static readonly WindowKeyInput Escape = new("Escape", "Escape", false, false, false, false);
    private static readonly WindowKeyInput Enter = new("Enter", "Enter", false, false, false, false);
    private static readonly WindowKeyInput Right = new("ArrowRight", "ArrowRight", false, false, false, false);
    private static readonly WindowKeyInput Down = new("ArrowDown", "ArrowDown", false, false, false, false);
    private static readonly WindowKeyInput AltBackquote = new("`", "Backquote", false, true, false, false);

    [Fact]
    public async Task Switching_selects_the_next_window_and_focuses_it_when_alt_is_released()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out WindowId[] ids, 3);

        await commands.ExecuteAsync(WindowKeyboardAction.SwitchNext);
        Assert.Equal(WindowKeyboardMode.Switching, commands.Mode);
        Assert.Equal(ids[1], commands.SwitcherSelection);

        commands.HandleGrabbedKey(AltBackquote);
        Assert.Equal(ids[0], commands.SwitcherSelection);

        commands.HandleAltReleased();
        Assert.Equal(WindowKeyboardMode.None, commands.Mode);
        Assert.Equal(ids[0], Focused(runtime));
    }

    [Fact]
    public async Task Escape_cancels_the_switcher_without_changing_the_focus()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out WindowId[] ids, 2);

        await commands.ExecuteAsync(WindowKeyboardAction.SwitchNext);
        commands.HandleGrabbedKey(Escape);

        Assert.Equal(WindowKeyboardMode.None, commands.Mode);
        Assert.Equal(ids[^1], Focused(runtime));
    }

    [Fact]
    public async Task Switching_to_a_minimized_window_restores_it()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out WindowId[] ids, 2);
        runtime.Apply(new MinimizeWindowCommand(ids[0]));

        await commands.ExecuteAsync(WindowKeyboardAction.SwitchNext);
        commands.HandleAltReleased();

        WindowRuntimeState restored = runtime.Windows.Single(window => window.Id == ids[0]);
        Assert.Equal(WindowVisualState.Normal, restored.VisualState);
        Assert.True(restored.IsFocused);
    }

    [Fact]
    public async Task Close_goes_through_the_shell_close_routine()
    {
        (_, WindowKeyboardCommands commands) = Create(out WindowId[] ids, 2);
        List<WindowId> closed = [];
        commands.CloseWindow = id =>
        {
            closed.Add(id);
            return Task.CompletedTask;
        };

        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.Close));

        Assert.Equal([ids[^1]], closed);
    }

    [Fact]
    public async Task Keyboard_move_follows_the_arrows_and_escape_puts_the_window_back()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out WindowId[] ids, 1);
        WindowBounds origin = runtime.Windows[0].Bounds;

        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.Move));
        commands.HandleGrabbedKey(Right);
        commands.HandleGrabbedKey(Down);
        Assert.Equal(origin.X + 10, runtime.Windows[0].Bounds.X);
        Assert.Equal(origin.Y + 10, runtime.Windows[0].Bounds.Y);

        commands.HandleGrabbedKey(Escape);
        Assert.Equal(origin, runtime.Windows[0].Bounds);
        Assert.Equal(WindowKeyboardMode.None, commands.Mode);
        _ = ids;
    }

    [Fact]
    public async Task Keyboard_resize_ends_with_one_completed_resize()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out _, 1);
        List<WindowEvent> events = [];
        runtime.EventsApplied += batch => events.AddRange(batch);
        WindowBounds origin = runtime.Windows[0].Bounds;

        await commands.ExecuteAsync(WindowKeyboardAction.Resize);
        commands.HandleGrabbedKey(Right);
        commands.HandleGrabbedKey(Right);
        commands.HandleGrabbedKey(Enter);

        Assert.Equal(origin.Width + 20, runtime.Windows[0].Bounds.Width);
        WindowResizeCompletedEvent completed = Assert.Single(events.OfType<WindowResizeCompletedEvent>());
        Assert.Equal(origin.Width + 20, completed.Bounds.Width);
    }

    [Fact]
    public async Task Snap_left_tiles_the_window_on_the_left_half()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out _, 1);

        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.SnapLeft));

        Assert.Equal(new WindowBounds(0, 0, 640, 720), runtime.Windows[0].Bounds);
        Assert.True(runtime.Windows[0].IsSnapped);
    }

    [Fact]
    public async Task Restore_or_minimize_undoes_a_snap()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out _, 1);
        WindowBounds origin = runtime.Windows[0].Bounds;
        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.SnapRight));

        Assert.True(await commands.ExecuteAsync(WindowKeyboardAction.RestoreOrMinimize));

        Assert.Equal(WindowVisualState.Normal, runtime.Windows[0].VisualState);
        Assert.False(runtime.Windows[0].IsSnapped);
        Assert.Equal(origin, runtime.Windows[0].Bounds);
    }

    [Fact]
    public async Task A_fixed_size_window_is_neither_maximized_nor_resized()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        runtime.Apply(new CreateWindowCommand(CreateState(1, resizable: false)));
        WindowKeyboardCommands commands = new(runtime);

        Assert.False(await commands.ExecuteAsync(WindowKeyboardAction.ToggleMaximize));
        Assert.False(await commands.ExecuteAsync(WindowKeyboardAction.Resize));
        Assert.False(await commands.ExecuteAsync(WindowKeyboardAction.SnapRight));
        Assert.Equal(WindowVisualState.Normal, runtime.Windows[0].VisualState);
    }

    [Fact]
    public async Task Super_down_restores_a_maximized_window_then_minimizes_it()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out _, 1);
        await commands.ExecuteAsync(WindowKeyboardAction.Maximize);
        Assert.Equal(WindowVisualState.Maximized, runtime.Windows[0].VisualState);

        await commands.ExecuteAsync(WindowKeyboardAction.RestoreOrMinimize);
        Assert.Equal(WindowVisualState.Normal, runtime.Windows[0].VisualState);

        await commands.ExecuteAsync(WindowKeyboardAction.RestoreOrMinimize);
        Assert.Equal(WindowVisualState.Minimized, runtime.Windows[0].VisualState);
    }

    [Fact]
    public void Linux_keys_are_active_only_under_the_keyboard_lock()
    {
        WindowKeyboardCommands commands = new(new WindowRuntime(new WindowBounds(0, 0, 1280, 720)));

        Assert.All(commands.ActiveBindings, binding => Assert.Equal(WindowKeyBindingSet.Fallback, binding.Set));
        Assert.Equal("Alt+Q", commands.ShortcutOf(WindowKeyboardAction.Close));

        commands.SetHostKeyboardState(fullScreen: true, locked: true);

        Assert.Contains(commands.ActiveBindings, binding => binding.Set == WindowKeyBindingSet.Linux && binding.Chord.ToString() == "Alt+Tab");
        Assert.Equal("Alt+F4", commands.ShortcutOf(WindowKeyboardAction.Close));
    }

    [Fact]
    public async Task The_window_menu_lists_every_command_and_runs_the_chosen_one()
    {
        (WindowRuntime runtime, WindowKeyboardCommands commands) = Create(out WindowId[] ids, 1);

        await commands.ExecuteAsync(WindowKeyboardAction.WindowMenu);
        Assert.Equal(ids[0], commands.Menu?.WindowId);
        IReadOnlyList<WindowMenuItem> items = commands.GetMenuItems(ids[0]);
        Assert.Contains(items, item => item.Action == WindowKeyboardAction.Move && item.IsEnabled);
        Assert.Contains(items, item => item.Action == WindowKeyboardAction.Close && item.Shortcut == "Alt+Q");

        await commands.ExecuteForWindowAsync(WindowKeyboardAction.ToggleMaximize, ids[0]);

        Assert.Null(commands.Menu);
        Assert.Equal(WindowVisualState.Maximized, runtime.Windows[0].VisualState);
    }

    [Fact]
    public void The_window_menu_speaks_English_by_default_and_the_session_language_once_set()
    {
        (WindowRuntime runtime, WindowKeyboardCommands english) = Create(out WindowId[] ids, 1);
        Assert.Equal(
            ["Maximize", "Minimize", "Move", "Resize", "Snap left", "Snap right", "Full screen (Linux keys)", "Close"],
            english.GetMenuItems(ids[0]).Select(item => item.Label));

        WindowKeyboardCommands french = new(runtime, locale: Localization.TestLocalization.CreateSessionLocale("fr_FR.UTF-8"));
        Assert.Equal(
            ["Agrandir", "Réduire", "Déplacer", "Redimensionner", "Ancrer à gauche", "Ancrer à droite", "Plein écran (touches Linux)", "Fermer"],
            french.GetMenuItems(ids[0]).Select(item => item.Label));
    }

    private static (WindowRuntime Runtime, WindowKeyboardCommands Commands) Create(out WindowId[] ids, int count)
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        ids = new WindowId[count];
        for (int index = 0; index < count; index++)
        {
            WindowRuntimeState state = CreateState(index + 1);
            ids[index] = state.Id;
            runtime.Apply(new CreateWindowCommand(state));
        }

        return (runtime, new WindowKeyboardCommands(runtime));
    }

    private static WindowId? Focused(WindowRuntime runtime) => runtime.Windows.FirstOrDefault(window => window.IsFocused)?.Id;

    private static WindowRuntimeState CreateState(int seed, bool resizable = true) =>
        new(
            WindowId.FromGuid(Guid.Parse($"50000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"60000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(resizable, 320, 240));
}
