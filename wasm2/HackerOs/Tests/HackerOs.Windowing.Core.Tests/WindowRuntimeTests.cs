using HackerOs.Windowing.Core;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Windowing.Core.Tests;

public sealed class WindowRuntimeTests
{
    [Fact]
    public void Create_focus_and_minimize_keep_one_frontmost_visible_focus()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState first = CreateState(1);
        WindowRuntimeState second = CreateState(2);

        runtime.Apply(new CreateWindowCommand(first));
        runtime.Apply(new CreateWindowCommand(second));
        IReadOnlyList<WindowEvent> events = runtime.Apply(new MinimizeWindowCommand(second.Id));

        Assert.Equal(2, runtime.Windows.Select(window => window.ZOrder).Distinct().Count());
        Assert.Equal(first.Id, Assert.Single(runtime.Windows, window => window.IsFocused).Id);
        Assert.Equal(WindowVisualState.Minimized, runtime.Windows.Single(window => window.Id == second.Id).VisualState);
        Assert.Collection(
            events,
            item => Assert.IsType<WindowVisualStateChangedEvent>(item),
            item => Assert.Equal(first.Id, Assert.IsType<WindowFocusedEvent>(item).WindowId));
    }

    [Fact]
    public void Maximize_restore_and_viewport_change_preserve_restore_geometry()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));

        runtime.Apply(new MaximizeWindowCommand(initial.Id));
        runtime.Apply(new ChangeViewportCommand(new WindowBounds(0, 40, 1024, 728)));
        WindowRuntimeState maximized = Assert.Single(runtime.Windows);
        Assert.Equal(new WindowBounds(0, 40, 1024, 728), maximized.Bounds);
        WindowBounds constrainedRestoreBounds = new(20, 40, 640, 480);
        Assert.Equal(constrainedRestoreBounds, maximized.RestoreBounds);

        runtime.Apply(new RestoreWindowCommand(initial.Id));
        WindowRuntimeState restored = Assert.Single(runtime.Windows);
        Assert.Equal(constrainedRestoreBounds, restored.Bounds);
        Assert.Null(restored.RestoreBounds);
    }

    [Fact]
    public void Restore_gives_a_snapped_window_its_geometry_from_before_the_snap()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));

        IReadOnlyList<WindowEvent> snapEvents = runtime.Apply(new SnapWindowCommand(initial.Id, new WindowBounds(0, 0, 640, 720)));
        WindowRuntimeState snapped = Assert.Single(runtime.Windows);
        Assert.True(snapped.IsSnapped);
        Assert.Equal(WindowVisualState.Normal, snapped.VisualState);
        Assert.Equal(new WindowBounds(0, 0, 640, 720), snapped.Bounds);
        Assert.Equal(initial.Bounds, snapped.RestoreBounds);
        Assert.IsType<WindowResizeCompletedEvent>(snapEvents[^1]);

        // A second snap keeps the geometry from before the first one.
        runtime.Apply(new SnapWindowCommand(initial.Id, new WindowBounds(640, 0, 640, 720)));
        Assert.Equal(initial.Bounds, Assert.Single(runtime.Windows).RestoreBounds);

        IReadOnlyList<WindowEvent> restoreEvents = runtime.Apply(new RestoreWindowCommand(initial.Id));
        WindowRuntimeState restored = Assert.Single(runtime.Windows);
        Assert.False(restored.IsSnapped);
        Assert.Equal(initial.Bounds, restored.Bounds);
        Assert.Null(restored.RestoreBounds);
        Assert.Equal(initial.Bounds, Assert.IsType<WindowResizeCompletedEvent>(restoreEvents[^1]).Bounds);
    }

    [Fact]
    public void Maximizing_a_snapped_window_then_restoring_it_undoes_the_snap()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));
        runtime.Apply(new SnapWindowCommand(initial.Id, new WindowBounds(0, 0, 640, 720)));

        runtime.Apply(new MaximizeWindowCommand(initial.Id));
        WindowRuntimeState maximized = Assert.Single(runtime.Windows);
        Assert.False(maximized.IsSnapped);
        Assert.Equal(initial.Bounds, maximized.RestoreBounds);

        runtime.Apply(new RestoreWindowCommand(initial.Id));
        Assert.Equal(initial.Bounds, Assert.Single(runtime.Windows).Bounds);
    }

    [Fact]
    public void Snapping_a_maximized_window_keeps_its_geometry_from_before_the_maximize()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));
        runtime.Apply(new MaximizeWindowCommand(initial.Id));

        IReadOnlyList<WindowEvent> events = runtime.Apply(new SnapWindowCommand(initial.Id, new WindowBounds(640, 0, 640, 720)));

        WindowRuntimeState snapped = Assert.Single(runtime.Windows);
        Assert.Equal(WindowVisualState.Normal, snapped.VisualState);
        Assert.True(snapped.IsSnapped);
        Assert.Equal(initial.Bounds, snapped.RestoreBounds);
        Assert.Equal(WindowVisualState.Normal, Assert.IsType<WindowVisualStateChangedEvent>(events[0]).State);
    }

    [Fact]
    public void A_minimized_snapped_window_comes_back_on_its_tile()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));
        runtime.Apply(new SnapWindowCommand(initial.Id, new WindowBounds(0, 0, 640, 720)));

        runtime.Apply(new MinimizeWindowCommand(initial.Id));
        runtime.Apply(new RestoreWindowCommand(initial.Id));

        WindowRuntimeState back = Assert.Single(runtime.Windows);
        Assert.True(back.IsSnapped);
        Assert.Equal(new WindowBounds(0, 0, 640, 720), back.Bounds);
        Assert.Equal(initial.Bounds, back.RestoreBounds);
    }

    [Fact]
    public void Moving_a_snapped_window_takes_it_off_its_tile_with_its_current_size()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));
        runtime.Apply(new SnapWindowCommand(initial.Id, new WindowBounds(0, 0, 640, 720)));

        runtime.Apply(new MoveWindowCommand(initial.Id, 100, 0));

        WindowRuntimeState moved = Assert.Single(runtime.Windows);
        Assert.False(moved.IsSnapped);
        Assert.Null(moved.RestoreBounds);
        Assert.Equal(new WindowBounds(100, 0, 640, 720), moved.Bounds);
    }

    [Fact]
    public void Snap_throws_for_a_non_resizable_or_minimized_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState fixedSize = CreateState(1, constraints: new WindowConstraints(false, 320, 240, 1200, 900));
        WindowRuntimeState minimized = CreateState(2);
        runtime.Apply(new CreateWindowCommand(fixedSize));
        runtime.Apply(new CreateWindowCommand(minimized));
        runtime.Apply(new MinimizeWindowCommand(minimized.Id));

        Assert.Throws<InvalidOperationException>(
            () => runtime.Apply(new SnapWindowCommand(fixedSize.Id, new WindowBounds(0, 0, 640, 720))));
        Assert.Throws<InvalidOperationException>(
            () => runtime.Apply(new SnapWindowCommand(minimized.Id, new WindowBounds(0, 0, 640, 720))));
    }

    [Fact]
    public void Resize_enforces_constraints_and_normal_state()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));

        runtime.Apply(new ResizeWindowCommand(initial.Id, new WindowBounds(20, 20, 200, 200)));
        Assert.Equal(320, Assert.Single(runtime.Windows).Bounds.Width);
        runtime.Apply(new MaximizeWindowCommand(initial.Id));
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(new MoveWindowCommand(initial.Id, 50, 50)));
    }

    [Fact]
    public void Resize_throws_for_a_non_resizable_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1, constraints: new WindowConstraints(false, 320, 240, 1200, 900));
        runtime.Apply(new CreateWindowCommand(initial));

        Assert.Throws<InvalidOperationException>(
            () => runtime.Apply(new ResizeWindowCommand(initial.Id, new WindowBounds(20, 20, 200, 200))));
    }

    [Fact]
    public void Move_throws_for_a_pinned_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1, constraints: new WindowConstraints(true, 320, 240, isMovable: false));
        runtime.Apply(new CreateWindowCommand(initial));

        Assert.Throws<InvalidOperationException>(() => runtime.Apply(new MoveWindowCommand(initial.Id, 50, 50)));
    }

    [Fact]
    public void Move_resize_and_mobile_viewport_keep_window_inside_work_area()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));

        runtime.Apply(new MoveWindowCommand(initial.Id, 1200, 700));
        Assert.Equal(new WindowBounds(640, 240, 640, 480), Assert.Single(runtime.Windows).Bounds);

        runtime.Apply(new ChangeViewportCommand(new WindowBounds(0, 0, 375, 600)));
        Assert.Equal(new WindowBounds(0, 120, 375, 480), Assert.Single(runtime.Windows).Bounds);
    }

    [Fact]
    public void Close_request_is_idempotent_and_force_close_refocuses()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState first = CreateState(1);
        WindowRuntimeState second = CreateState(2);
        runtime.Apply(new CreateWindowCommand(first));
        runtime.Apply(new CreateWindowCommand(second));

        Assert.Single(runtime.Apply(new RequestWindowCloseCommand(second.Id)));
        Assert.Empty(runtime.Apply(new RequestWindowCloseCommand(second.Id)));
        IReadOnlyList<WindowEvent> events = runtime.Apply(new ForceWindowCloseCommand(second.Id));

        Assert.Equal(first.Id, Assert.Single(runtime.Windows).Id);
        Assert.True(runtime.Windows[0].IsFocused);
        Assert.False(Assert.IsType<WindowClosedEvent>(events[0]).WasForced);
        Assert.Equal(first.Id, Assert.IsType<WindowFocusedEvent>(events[1]).WindowId);
    }

    [Fact]
    public void Cancel_close_clears_pending_close_without_removing_window()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new RequestWindowCloseCommand(window.Id));

        Assert.IsType<WindowCloseCancelledEvent>(Assert.Single(runtime.Apply(new CancelWindowCloseCommand(window.Id))));
        Assert.Empty(runtime.Apply(new CancelWindowCloseCommand(window.Id)));
        Assert.Single(runtime.Windows);
        Assert.True(Assert.IsType<WindowClosedEvent>(runtime.Apply(new ForceWindowCloseCommand(window.Id))[0]).WasForced);
    }

    [Fact]
    public void Owner_modal_window_blocks_owner_and_returns_focus_when_closed()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState owner = CreateState(1);
        runtime.Apply(new CreateWindowCommand(owner));
        WindowRuntimeState modal = CreateState(2, WindowModality.OwnerModal, owner.Id);
        runtime.Apply(new CreateWindowCommand(modal));

        Assert.True(runtime.IsInteractionBlocked(owner.Id));
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(new FocusWindowCommand(owner.Id)));

        runtime.Apply(new ForceWindowCloseCommand(modal.Id));
        Assert.False(runtime.IsInteractionBlocked(owner.Id));
        Assert.True(Assert.Single(runtime.Windows).IsFocused);
    }

    [Fact]
    public void Completing_a_resize_reports_the_final_bounds_once()
    {
        // ADR 0095 §1: intermediate resizes of a gesture are facts, the end of the gesture is one more.
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState initial = CreateState(1);
        runtime.Apply(new CreateWindowCommand(initial));
        List<IReadOnlyList<WindowEvent>> batches = [];
        runtime.EventsApplied += batches.Add;

        runtime.Apply(new ResizeWindowCommand(initial.Id, new WindowBounds(20, 30, 700, 500)));
        runtime.Apply(new ResizeWindowCommand(initial.Id, new WindowBounds(20, 30, 720, 510)));
        IReadOnlyList<WindowEvent> completed = runtime.Apply(new CompleteWindowResizeCommand(initial.Id));

        Assert.Equal(3, batches.Count);
        WindowResizeCompletedEvent end = Assert.IsType<WindowResizeCompletedEvent>(Assert.Single(completed));
        Assert.Equal(new WindowBounds(20, 30, 720, 510), end.Bounds);
    }

    [Fact]
    public void Minimizing_a_background_window_keeps_the_focus_where_it_is()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState first = CreateState(1);
        WindowRuntimeState second = CreateState(2);
        runtime.Apply(new CreateWindowCommand(first));
        runtime.Apply(new CreateWindowCommand(second));

        IReadOnlyList<WindowEvent> events = runtime.Apply(new MinimizeWindowCommand(first.Id));

        Assert.IsType<WindowVisualStateChangedEvent>(Assert.Single(events));
        Assert.Equal(second.Id, Assert.Single(runtime.Windows, window => window.IsFocused).Id);
    }

    [Fact]
    public void A_minimum_larger_than_the_work_area_is_kept_and_the_window_overflows()
    {
        // ADR 0095 §2: the declared minimum wins on a small screen, as on a real one.
        WindowRuntime runtime = new(new WindowBounds(0, 0, 400, 300));
        WindowRuntimeState initial = CreateState(1, constraints: new WindowConstraints(true, 480, 360));

        runtime.Apply(new CreateWindowCommand(initial));

        WindowRuntimeState window = Assert.Single(runtime.Windows);
        Assert.Equal(new WindowBounds(0, 0, 480, 360), window.Bounds);
        Assert.Equal(new WindowBounds(0, 0, 400, 300), runtime.WorkArea);
    }

    private static WindowRuntimeState CreateState(
        int seed,
        WindowModality modality = WindowModality.Modeless,
        WindowId? ownerId = null,
        WindowConstraints? constraints = null) =>
        new(
            WindowId.FromGuid(Guid.Parse($"10000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"20000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            constraints ?? new WindowConstraints(true, 320, 240, 1200, 900),
            modality,
            ownerId);
}