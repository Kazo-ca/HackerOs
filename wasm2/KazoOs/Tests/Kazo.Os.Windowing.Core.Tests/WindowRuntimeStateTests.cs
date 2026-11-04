using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Windowing.Core.Tests;

public sealed class WindowRuntimeStateTests
{
    [Fact]
    public void Constructor_preserves_complete_authoritative_snapshot()
    {
        WindowId id = WindowId.FromGuid(Guid.Parse("10000000-0000-0000-0000-000000000001"));
        WindowId ownerId = WindowId.FromGuid(Guid.Parse("10000000-0000-0000-0000-000000000002"));
        WindowBounds bounds = new(20, 30, 800, 600);
        WindowBounds restoreBounds = new(40, 50, 640, 480);
        WindowConstraints constraints = new(true, 320, 240, 1600, 1200);

        WindowOwnerId ownerInstanceId = WindowOwnerId.FromGuid(Guid.Parse("20000000-0000-0000-0000-000000000001"));
        WindowRuntimeState state = new(
            id,
            "org.hackeros.terminal",
            ownerInstanceId,
            "Terminal",
            "icons/terminal.svg",
            bounds,
            restoreBounds,
            7,
            WindowVisualState.Maximized,
            constraints,
            WindowModality.OwnerModal,
            ownerId,
            true);

        Assert.Equal(id, state.Id);
        Assert.Equal("org.hackeros.terminal", state.AppId);
        Assert.Equal(ownerInstanceId, state.OwnerInstanceId);
        Assert.Equal("Terminal", state.Title);
        Assert.Equal(bounds, state.Bounds);
        Assert.Equal(restoreBounds, state.RestoreBounds);
        Assert.Equal(7, state.ZOrder);
        Assert.Equal(WindowVisualState.Maximized, state.VisualState);
        Assert.Same(constraints, state.Constraints);
        Assert.Equal(ownerId, state.OwnerId);
        Assert.True(state.IsFocused);
    }

    [Fact]
    public void Value_objects_reject_invalid_geometry_and_constraints()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowBounds(0, 0, 0, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowBounds(double.NaN, 0, 800, 600));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowConstraints(true, 320, 240, 300, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowId.FromGuid(Guid.Empty));
    }

    [Fact]
    public void Constraints_isMovable_defaults_to_true_for_source_compatibility()
    {
        WindowConstraints constraints = new(true, 320, 240);

        Assert.True(constraints.IsMovable);
    }

    [Fact]
    public void Constraints_isMovable_can_be_pinned_false()
    {
        WindowConstraints constraints = new(true, 320, 240, isMovable: false);

        Assert.False(constraints.IsMovable);
    }

    [Fact]
    public void SupportsBack_defaults_to_false_for_source_compatibility()
    {
        WindowRuntimeState state = CreateState(WindowId.FromGuid(Guid.NewGuid()), WindowModality.Modeless, null);

        Assert.False(state.SupportsBack);
    }

    [Fact]
    public void SupportsBack_can_be_declared_true()
    {
        WindowRuntimeState state = new(
            WindowId.FromGuid(Guid.NewGuid()),
            "org.hackeros.settings",
            WindowOwnerId.FromGuid(Guid.NewGuid()),
            "Settings",
            null,
            new WindowBounds(10, 10, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            supportsBack: true);

        Assert.True(state.SupportsBack);
    }

    [Fact]
    public void Owner_modal_window_requires_a_distinct_owner()
    {
        WindowId id = WindowId.FromGuid(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => CreateState(id, WindowModality.OwnerModal, null));
        Assert.Throws<ArgumentException>(() => CreateState(id, WindowModality.OwnerModal, id));
    }

    [Fact]
    public void Commands_and_events_are_renderer_independent_value_contracts()
    {
        WindowId id = WindowId.FromGuid(Guid.NewGuid());
        WindowRuntimeState state = CreateState(id, WindowModality.Modeless, null);
        WindowBounds movedBounds = new(30, 40, 640, 480);

        WindowCommand command = new CreateWindowCommand(state);
        WindowEvent windowEvent = new WindowMovedEvent(id, state.Bounds, movedBounds);

        Assert.Equal(state, Assert.IsType<CreateWindowCommand>(command).InitialState);
        Assert.Equal(movedBounds, Assert.IsType<WindowMovedEvent>(windowEvent).Bounds);
        Assert.Equal(new FocusWindowCommand(id), new FocusWindowCommand(id));
    }

    private static WindowRuntimeState CreateState(WindowId id, WindowModality modality, WindowId? ownerId) =>
        new(
            id,
            "org.hackeros.settings",
            WindowOwnerId.FromGuid(Guid.NewGuid()),
            "Settings",
            null,
            new WindowBounds(10, 10, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240),
            modality,
            ownerId);
}