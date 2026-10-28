using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Windowing.Core.Tests;

/// <summary>Screens of the extended desktop in the window runtime (ADR 0178 §2).</summary>
public sealed class WindowRuntimeDisplayTests
{
    private static readonly WindowBounds PrimaryArea = new(0, 0, 1280, 720);
    private static readonly WindowBounds SecondArea = new(0, 0, 1920, 1040);
    private static readonly DisplayId Second = DisplayId.FromNumber(2);

    // CreateState(1) sits at (20, 30) on screen 1: the same offset in proportion on screen 2.
    private static readonly WindowBounds FirstWindowOnSecond = new(30, 30 * 1040.0 / 720, 640, 480);

    [Fact]
    public void The_default_display_is_screen_one()
    {
        Assert.Equal(DisplayId.Primary, default);
        Assert.Equal(1, DisplayId.Primary.Number);
        Assert.True(DisplayId.Primary.IsPrimary);
        Assert.False(Second.IsPrimary);
        Assert.Equal("2", Second.ToString());
        Assert.Equal(DisplayId.Primary, DisplayId.FromNumber(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DisplayId.FromNumber(0));
        Assert.Equal(DisplayId.Primary, CreateState(1).DisplayId);
    }

    [Fact]
    public void Adding_and_removing_screens_reuses_the_smallest_free_number()
    {
        WindowRuntime runtime = new(PrimaryArea);
        Assert.Equal([DisplayId.Primary], runtime.Displays);
        Assert.Equal(Second, runtime.NextDisplayId);

        IReadOnlyList<WindowEvent> added = runtime.Apply(new AddDisplayCommand(Second, SecondArea));
        runtime.Apply(new AddDisplayCommand(DisplayId.FromNumber(3), SecondArea));
        runtime.Apply(new RemoveDisplayCommand(Second));

        Assert.Equal(new DisplayAddedEvent(Second, SecondArea), Assert.Single(added));
        Assert.Equal([DisplayId.Primary, DisplayId.FromNumber(3)], runtime.Displays);
        Assert.Equal(Second, runtime.NextDisplayId);
        Assert.Throws<InvalidOperationException>(
            () => runtime.Apply(new AddDisplayCommand(DisplayId.FromNumber(3), SecondArea)));
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(new AddDisplayCommand(DisplayId.Primary, SecondArea)));
    }

    [Fact]
    public void A_viewport_change_only_touches_the_windows_of_its_screen()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState onPrimary = CreateState(1);
        WindowRuntimeState onSecond = CreateState(2) with { DisplayId = Second };
        runtime.Apply(new CreateWindowCommand(onPrimary));
        runtime.Apply(new CreateWindowCommand(onSecond));
        runtime.Apply(new MaximizeWindowCommand(onPrimary.Id));
        runtime.Apply(new MaximizeWindowCommand(onSecond.Id));

        WindowBounds smaller = new(0, 0, 1024, 600);
        WindowViewportChangedEvent changed = Assert.IsType<WindowViewportChangedEvent>(
            Assert.Single(runtime.Apply(new ChangeViewportCommand(smaller, Second))));

        Assert.Equal(Second, changed.DisplayId);
        Assert.Equal(smaller, runtime.GetWorkArea(Second));
        Assert.Equal(PrimaryArea, runtime.WorkArea);
        Assert.Equal(PrimaryArea, Find(runtime, onPrimary.Id).Bounds);
        Assert.Equal(smaller, Find(runtime, onSecond.Id).Bounds);
        Assert.Throws<KeyNotFoundException>(
            () => runtime.Apply(new ChangeViewportCommand(smaller, DisplayId.FromNumber(5))));
    }

    [Fact]
    public void The_existing_viewport_command_still_targets_screen_one()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowBounds area = new(0, 40, 1024, 728);

        WindowViewportChangedEvent changed = Assert.IsType<WindowViewportChangedEvent>(
            Assert.Single(runtime.Apply(new ChangeViewportCommand(area))));

        Assert.Equal(DisplayId.Primary, changed.DisplayId);
        Assert.Equal(area, runtime.WorkArea);
        Assert.Equal(SecondArea, runtime.GetWorkArea(Second));
    }

    [Fact]
    public void Maximizing_and_moving_use_the_work_area_of_the_window_screen()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1) with { DisplayId = Second };
        runtime.Apply(new CreateWindowCommand(window));

        runtime.Apply(new MoveWindowCommand(window.Id, 1500, 900));
        Assert.Equal(new WindowBounds(1280, 560, 640, 480), Find(runtime, window.Id).Bounds);

        runtime.Apply(new MaximizeWindowCommand(window.Id));
        Assert.Equal(SecondArea, Find(runtime, window.Id).Bounds);
    }

    [Fact]
    public void A_window_cannot_be_created_on_an_unknown_screen()
    {
        WindowRuntime runtime = new(PrimaryArea);

        Assert.Throws<KeyNotFoundException>(
            () => runtime.Apply(new CreateWindowCommand(CreateState(1) with { DisplayId = Second })));
    }

    [Fact]
    public void A_normal_window_keeps_its_relative_position_size_focus_and_stacking()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        WindowRuntimeState before = Find(runtime, window.Id);

        IReadOnlyList<WindowEvent> events = runtime.Apply(new MoveWindowToDisplayCommand(window.Id, Second));

        WindowRuntimeState after = Find(runtime, window.Id);
        Assert.Equal(
            new WindowDisplayChangedEvent(window.Id, DisplayId.Primary, Second, FirstWindowOnSecond, null),
            Assert.Single(events));
        Assert.Equal(Second, after.DisplayId);
        Assert.Equal(FirstWindowOnSecond, after.Bounds);
        Assert.Equal(WindowVisualState.Normal, after.VisualState);
        Assert.Equal(before.ZOrder, after.ZOrder);
        Assert.True(after.IsFocused);
    }

    [Fact]
    public void A_normal_window_is_kept_inside_a_smaller_target_screen()
    {
        WindowRuntime runtime = new(SecondArea);
        runtime.Apply(new AddDisplayCommand(Second, new WindowBounds(0, 0, 800, 600)));
        WindowRuntimeState window = CreateState(1, bounds: new WindowBounds(1000, 500, 1100, 500));
        runtime.Apply(new CreateWindowCommand(window));

        runtime.Apply(new MoveWindowToDisplayCommand(window.Id, Second));

        Assert.Equal(new WindowBounds(0, 100, 800, 500), Find(runtime, window.Id).Bounds);
    }

    [Fact]
    public void A_maximized_window_stays_maximized_and_keeps_a_mapped_restore_geometry()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new MaximizeWindowCommand(window.Id));

        WindowDisplayChangedEvent moved = Assert.IsType<WindowDisplayChangedEvent>(
            Assert.Single(runtime.Apply(new MoveWindowToDisplayCommand(window.Id, Second))));

        WindowRuntimeState after = Find(runtime, window.Id);
        Assert.Equal(WindowVisualState.Maximized, after.VisualState);
        Assert.Equal(SecondArea, after.Bounds);
        Assert.Equal(FirstWindowOnSecond, after.RestoreBounds);
        Assert.Equal(FirstWindowOnSecond, moved.RestoreBounds);

        runtime.Apply(new RestoreWindowCommand(window.Id));
        Assert.Equal(FirstWindowOnSecond, Find(runtime, window.Id).Bounds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_snapped_window_stays_on_the_same_half(bool left)
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new ResizeWindowCommand(window.Id, new WindowBounds(left ? 0 : 640, 0, 640, 720)));

        runtime.Apply(new MoveWindowToDisplayCommand(window.Id, Second));

        Assert.Equal(new WindowBounds(left ? 0 : 960, 0, 960, 1040), Find(runtime, window.Id).Bounds);
    }

    [Fact]
    public void A_minimized_window_changes_screen_and_stays_minimized()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState first = CreateState(1);
        WindowRuntimeState second = CreateState(2);
        runtime.Apply(new CreateWindowCommand(first));
        runtime.Apply(new CreateWindowCommand(second));
        runtime.Apply(new MaximizeWindowCommand(second.Id));
        runtime.Apply(new MinimizeWindowCommand(second.Id));

        runtime.Apply(new MoveWindowToDisplayCommand(second.Id, Second));

        WindowRuntimeState after = Find(runtime, second.Id);
        Assert.Equal(WindowVisualState.Minimized, after.VisualState);
        Assert.Equal(Second, after.DisplayId);
        Assert.Equal(SecondArea, after.Bounds);
        Assert.Equal(first.Id, Assert.Single(runtime.Windows, window => window.IsFocused).Id);
    }

    [Fact]
    public void Moving_to_the_same_screen_does_nothing_and_an_unknown_screen_throws()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        int notifications = 0;
        runtime.StateChanged += () => notifications++;

        Assert.Empty(runtime.Apply(new MoveWindowToDisplayCommand(window.Id, DisplayId.Primary)));
        Assert.Equal(0, notifications);
        Assert.Throws<KeyNotFoundException>(
            () => runtime.Apply(new MoveWindowToDisplayCommand(window.Id, DisplayId.FromNumber(3))));
    }

    [Fact]
    public void A_modal_window_and_its_owner_move_together()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState owner = CreateState(1);
        WindowRuntimeState modal = CreateState(2, WindowModality.OwnerModal, owner.Id);
        WindowRuntimeState nested = CreateState(3, WindowModality.OwnerModal, modal.Id);
        WindowRuntimeState unrelated = CreateState(4);
        runtime.Apply(new CreateWindowCommand(owner));
        runtime.Apply(new CreateWindowCommand(modal));
        runtime.Apply(new CreateWindowCommand(nested));
        runtime.Apply(new CreateWindowCommand(unrelated));

        IReadOnlyList<WindowEvent> events = runtime.Apply(new MoveWindowToDisplayCommand(modal.Id, Second));

        Assert.Equal(
            [owner.Id, modal.Id, nested.Id],
            events.Cast<WindowDisplayChangedEvent>().Select(change => change.WindowId));
        Assert.All([owner.Id, modal.Id, nested.Id], id => Assert.Equal(Second, Find(runtime, id).DisplayId));
        Assert.Equal(DisplayId.Primary, Find(runtime, unrelated.Id).DisplayId);

        runtime.Apply(new MoveWindowToDisplayCommand(owner.Id, DisplayId.Primary));
        Assert.All([owner.Id, modal.Id, nested.Id], id => Assert.Equal(DisplayId.Primary, Find(runtime, id).DisplayId));
    }

    [Fact]
    public void A_new_modal_window_opens_on_its_owner_screen()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState owner = CreateState(1) with { DisplayId = Second };
        runtime.Apply(new CreateWindowCommand(owner));

        runtime.Apply(new CreateWindowCommand(CreateState(2, WindowModality.OwnerModal, owner.Id)));

        Assert.All(runtime.Windows, window => Assert.Equal(Second, window.DisplayId));
    }

    [Fact]
    public void Removing_a_screen_brings_its_windows_back_to_screen_one_in_their_state()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState normal = CreateState(1, bounds: new WindowBounds(960, 520, 640, 480)) with { DisplayId = Second };
        WindowRuntimeState maximized = CreateState(2) with { DisplayId = Second };
        WindowRuntimeState minimized = CreateState(3) with { DisplayId = Second };
        WindowRuntimeState stays = CreateState(4);
        foreach (WindowRuntimeState window in new[] { normal, maximized, minimized, stays })
        {
            runtime.Apply(new CreateWindowCommand(window));
        }

        runtime.Apply(new MaximizeWindowCommand(maximized.Id));
        runtime.Apply(new MinimizeWindowCommand(minimized.Id));
        runtime.Apply(new FocusWindowCommand(stays.Id));

        IReadOnlyList<WindowEvent> events = runtime.Apply(new RemoveDisplayCommand(Second));

        Assert.Equal(new DisplayRemovedEvent(Second), events[^1]);
        Assert.Equal(3, events.OfType<WindowDisplayChangedEvent>().Count());
        Assert.Equal([DisplayId.Primary], runtime.Displays);
        Assert.All(runtime.Windows, window => Assert.Equal(DisplayId.Primary, window.DisplayId));
        Assert.Equal(new WindowBounds(640, 240, 640, 480), Find(runtime, normal.Id).Bounds);
        Assert.Equal(WindowVisualState.Maximized, Find(runtime, maximized.Id).VisualState);
        Assert.Equal(PrimaryArea, Find(runtime, maximized.Id).Bounds);
        Assert.Equal(WindowVisualState.Minimized, Find(runtime, minimized.Id).VisualState);
        Assert.Equal(stays.Id, Assert.Single(runtime.Windows, window => window.IsFocused).Id);
    }

    [Fact]
    public void A_snapped_window_stays_snapped_on_the_target_screen_with_its_geometry_from_before_the_snap()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new SnapWindowCommand(window.Id, new WindowBounds(640, 0, 640, 720)));

        runtime.Apply(new MoveWindowToDisplayCommand(window.Id, Second));

        WindowRuntimeState moved = Find(runtime, window.Id);
        Assert.True(moved.IsSnapped);
        Assert.Equal(new WindowBounds(960, 0, 960, 1040), moved.Bounds);
        Assert.Equal(FirstWindowOnSecond, moved.RestoreBounds);

        runtime.Apply(new RestoreWindowCommand(window.Id));

        WindowRuntimeState restored = Find(runtime, window.Id);
        Assert.False(restored.IsSnapped);
        Assert.Equal(FirstWindowOnSecond, restored.Bounds);
    }

    [Fact]
    public void Snapping_on_a_secondary_screen_uses_that_screen_work_area()
    {
        WindowRuntime runtime = RuntimeWithSecondScreen();
        WindowRuntimeState window = CreateState(1, bounds: new WindowBounds(100, 100, 640, 480)) with { DisplayId = Second };
        runtime.Apply(new CreateWindowCommand(window));

        runtime.Apply(new SnapWindowCommand(window.Id, new WindowBounds(0, 0, 960, 1040)));

        Assert.Equal(new WindowBounds(0, 0, 960, 1040), Find(runtime, window.Id).Bounds);
    }

    [Fact]
    public void Screen_one_cannot_be_removed_and_an_unknown_screen_is_ignored()
    {
        WindowRuntime runtime = new(PrimaryArea);

        Assert.Throws<InvalidOperationException>(() => runtime.Apply(new RemoveDisplayCommand(DisplayId.Primary)));
        Assert.Empty(runtime.Apply(new RemoveDisplayCommand(Second)));
    }

    private static WindowRuntime RuntimeWithSecondScreen()
    {
        WindowRuntime runtime = new(PrimaryArea);
        runtime.Apply(new AddDisplayCommand(Second, SecondArea));
        return runtime;
    }

    private static WindowRuntimeState Find(WindowRuntime runtime, WindowId id) =>
        runtime.Windows.Single(window => window.Id == id);

    private static WindowRuntimeState CreateState(
        int seed,
        WindowModality modality = WindowModality.Modeless,
        WindowId? ownerId = null,
        WindowBounds? bounds = null) =>
        new(
            WindowId.FromGuid(Guid.Parse($"10000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"20000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            bounds ?? new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 2000, 1200),
            modality,
            ownerId);
}
