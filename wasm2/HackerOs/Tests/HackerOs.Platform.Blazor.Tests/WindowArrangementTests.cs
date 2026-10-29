using HackerOs.Platform.Blazor.Windows;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>ADR 0194 §4-5: « Afficher le bureau », cascade and tile act on the windows of one screen.</summary>
public sealed class WindowArrangementTests
{
    private static readonly WindowBounds Area = new(0, 0, 1200, 800);
    private static readonly DisplayId Screen2 = DisplayId.FromNumber(2);

    [Fact]
    public void Show_desktop_minimizes_the_windows_of_its_screen_only()
    {
        WindowRuntime runtime = NewRuntime();
        WindowId first = Open(runtime, 1);
        WindowId second = Open(runtime, 2);
        WindowId other = Open(runtime, 3, Screen2);

        Assert.Equal(2, WindowArrangement.ShowDesktop(runtime, DisplayId.Primary));

        Assert.Equal(WindowVisualState.Minimized, State(runtime, first).VisualState);
        Assert.Equal(WindowVisualState.Minimized, State(runtime, second).VisualState);
        Assert.Equal(WindowVisualState.Normal, State(runtime, other).VisualState);
    }

    [Fact]
    public void Cascade_steps_each_window_from_the_corner_and_keeps_its_size_and_order()
    {
        WindowRuntime runtime = NewRuntime();
        WindowId back = Open(runtime, 1, bounds: new WindowBounds(500, 300, 400, 300));
        WindowId front = Open(runtime, 2, bounds: new WindowBounds(100, 100, 640, 480));
        runtime.Apply(new FocusWindowCommand(back));
        runtime.Apply(new FocusWindowCommand(front));

        Assert.Equal(2, WindowArrangement.Cascade(runtime, DisplayId.Primary));

        Assert.Equal(new WindowBounds(0, 0, 400, 300), State(runtime, back).Bounds);
        Assert.Equal(new WindowBounds(WindowLaunchCoordinator.CascadeStep, WindowLaunchCoordinator.CascadeStep, 640, 480), State(runtime, front).Bounds);
        Assert.Equal([back, front], runtime.Windows.Select(window => window.Id));
    }

    [Fact]
    public void Tile_gives_each_resizable_window_a_cell_of_the_grid()
    {
        WindowRuntime runtime = NewRuntime();
        WindowId first = Open(runtime, 1);
        WindowId second = Open(runtime, 2);
        WindowId third = Open(runtime, 3);
        runtime.Apply(new FocusWindowCommand(first));
        runtime.Apply(new FocusWindowCommand(second));
        runtime.Apply(new FocusWindowCommand(third));

        Assert.Equal(3, WindowArrangement.Tile(runtime, DisplayId.Primary));

        // Three windows: two columns, two rows.
        Assert.Equal(new WindowBounds(0, 0, 600, 400), State(runtime, first).Bounds);
        Assert.Equal(new WindowBounds(600, 0, 600, 400), State(runtime, second).Bounds);
        Assert.Equal(new WindowBounds(0, 400, 600, 400), State(runtime, third).Bounds);
    }

    [Fact]
    public void Tile_restores_a_maximized_window_and_takes_a_snapped_one_off_its_tile_without_changing_the_order()
    {
        WindowRuntime runtime = NewRuntime();
        WindowId maximized = Open(runtime, 1);
        WindowId snapped = Open(runtime, 2);
        runtime.Apply(new MaximizeWindowCommand(maximized));
        runtime.Apply(new SnapWindowCommand(snapped, new WindowBounds(600, 0, 600, 800)));
        runtime.Apply(new FocusWindowCommand(snapped));

        WindowArrangement.Tile(runtime, DisplayId.Primary);

        WindowRuntimeState first = State(runtime, maximized);
        WindowRuntimeState second = State(runtime, snapped);
        Assert.Equal(WindowVisualState.Normal, first.VisualState);
        Assert.Equal(new WindowBounds(0, 0, 600, 800), first.Bounds);
        Assert.False(second.IsSnapped);
        Assert.Equal(new WindowBounds(600, 0, 600, 800), second.Bounds);
        Assert.Equal([maximized, snapped], runtime.Windows.Select(window => window.Id));
        Assert.True(second.IsFocused);
    }

    [Fact]
    public void A_fixed_size_window_moves_toward_its_cell_and_keeps_its_size()
    {
        WindowRuntime runtime = NewRuntime();
        WindowId first = Open(runtime, 1);
        WindowId fixedSize = Open(runtime, 2, constraints: new WindowConstraints(false, 300, 200));
        runtime.Apply(new FocusWindowCommand(first));
        runtime.Apply(new FocusWindowCommand(fixedSize));

        WindowArrangement.Tile(runtime, DisplayId.Primary);

        Assert.Equal(new WindowBounds(0, 0, 600, 800), State(runtime, first).Bounds);
        // Its cell starts at x = 600; the work area keeps the 640 px window inside, at x = 560.
        Assert.Equal(new WindowBounds(560, 0, 640, 480), State(runtime, fixedSize).Bounds);
    }

    [Fact]
    public void Minimized_windows_dialogs_and_their_blocked_owners_are_left_alone()
    {
        WindowRuntime runtime = NewRuntime();
        WindowId owner = Open(runtime, 1);
        WindowId minimized = Open(runtime, 2, bounds: new WindowBounds(300, 300, 400, 300));
        runtime.Apply(new MinimizeWindowCommand(minimized));
        WindowId dialog = Open(runtime, 3, bounds: new WindowBounds(200, 200, 400, 300), owner: owner);

        Assert.Empty(WindowArrangement.ArrangeableWindows(runtime, DisplayId.Primary));
        Assert.Equal(0, WindowArrangement.Cascade(runtime, DisplayId.Primary));

        Assert.Equal(new WindowBounds(40, 40, 640, 480), State(runtime, owner).Bounds);
        Assert.Equal(new WindowBounds(300, 300, 400, 300), State(runtime, minimized).Bounds);
        Assert.Equal(new WindowBounds(200, 200, 400, 300), State(runtime, dialog).Bounds);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 1200, 800)]
    [InlineData(2, 1, 600, 0, 600, 800)]
    [InlineData(4, 3, 600, 400, 600, 400)]
    [InlineData(5, 4, 400, 400, 400, 400)]
    public void Tile_cells_follow_a_grid_of_ceil_sqrt_n_columns(int count, int index, double x, double y, double width, double height)
    {
        Assert.Equal(new WindowBounds(x, y, width, height), WindowArrangement.TileCell(Area, index, count));
    }

    private static WindowRuntime NewRuntime()
    {
        WindowRuntime runtime = new(Area);
        runtime.Apply(new AddDisplayCommand(Screen2, Area));
        return runtime;
    }

    private static WindowRuntimeState State(WindowRuntime runtime, WindowId id) =>
        runtime.Windows.Single(window => window.Id == id);

    private static WindowId Open(
        WindowRuntime runtime,
        int number,
        DisplayId? display = null,
        WindowBounds? bounds = null,
        WindowConstraints? constraints = null,
        WindowId? owner = null)
    {
        WindowId id = WindowId.FromGuid(Guid.Parse($"a1000000-0000-0000-0000-00000000000{number}"));
        WindowRuntimeState state = new(
            id,
            "org.hackeros.test",
            WindowOwnerId.FromGuid(Guid.Parse($"b1000000-0000-0000-0000-00000000000{number}")),
            $"Window {number}",
            null,
            bounds ?? new WindowBounds(40, 40, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            constraints ?? new WindowConstraints(true, 320, 240),
            owner is null ? WindowModality.Modeless : WindowModality.OwnerModal,
            owner);
        runtime.Apply(new CreateWindowCommand(state with { DisplayId = display ?? DisplayId.Primary }));
        return id;
    }
}
