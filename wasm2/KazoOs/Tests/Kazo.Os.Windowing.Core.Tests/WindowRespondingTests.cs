using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Windowing.Core.Tests;

/// <summary>ADR 0127 §2-3: the "not responding" flag of a window is display state only.</summary>
public sealed class WindowRespondingTests
{
    [Fact]
    public void A_new_window_responds()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        runtime.Apply(new CreateWindowCommand(CreateState(1)));

        Assert.True(Assert.Single(runtime.Windows).IsResponding);
    }

    [Fact]
    public void Marking_a_window_reports_one_event_and_keeps_its_title()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));

        IReadOnlyList<WindowEvent> events = runtime.Apply(new SetWindowRespondingCommand(window.Id, false));

        WindowRespondingChangedEvent changed = Assert.IsType<WindowRespondingChangedEvent>(Assert.Single(events));
        Assert.Equal(new WindowRespondingChangedEvent(window.Id, false), changed);
        WindowRuntimeState marked = Assert.Single(runtime.Windows);
        Assert.False(marked.IsResponding);
        Assert.Equal("Test 1", marked.Title);
    }

    [Fact]
    public void An_unchanged_flag_produces_no_event()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));

        Assert.Empty(runtime.Apply(new SetWindowRespondingCommand(window.Id, true)));
    }

    [Fact]
    public void The_flag_survives_focus_moves_and_title_changes_until_cleared()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new SetWindowRespondingCommand(window.Id, false));

        runtime.Apply(new FocusWindowCommand(window.Id));
        runtime.Apply(new MoveWindowCommand(window.Id, 100, 100));
        runtime.SetTitle(window.Id, "renamed");
        Assert.False(Assert.Single(runtime.Windows).IsResponding);

        runtime.Apply(new SetWindowRespondingCommand(window.Id, true));
        Assert.True(Assert.Single(runtime.Windows).IsResponding);
    }

    private static WindowRuntimeState CreateState(int seed) =>
        new(
            WindowId.FromGuid(Guid.Parse($"10000000-0000-0000-0000-{seed:D12}")),
            $"org.kazoos.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"20000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 1200, 900));
}
