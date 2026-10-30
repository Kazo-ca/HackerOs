using HackerOs.Windowing.Abstractions;

namespace HackerOs.Windowing.Core.Tests;

/// <summary>ADR 0203 §4: the shell changes the icon of open windows when the icon pack changes.</summary>
public sealed class WindowIconTests
{
    [Fact]
    public void SetWindowIconCommand_replaces_the_icon_and_reports_the_change()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState("_content/App/icons/app.svg");
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new MaximizeWindowCommand(window.Id));
        WindowRuntimeState before = Assert.Single(runtime.Windows);

        IReadOnlyList<WindowEvent> events = runtime.Apply(new SetWindowIconCommand(window.Id, "data:image/png;base64,AA=="));

        WindowIconChangedEvent changed = Assert.IsType<WindowIconChangedEvent>(Assert.Single(events));
        Assert.Equal("_content/App/icons/app.svg", changed.PreviousIconAssetPath);
        WindowRuntimeState after = Assert.Single(runtime.Windows);
        Assert.Equal("data:image/png;base64,AA==", after.IconAssetPath);
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(before.VisualState, after.VisualState);
        Assert.Equal(before.Title, after.Title);
    }

    [Fact]
    public void An_unchanged_icon_produces_no_event_and_null_restores_the_generic_symbol()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState("a.png");
        runtime.Apply(new CreateWindowCommand(window));

        Assert.Empty(runtime.Apply(new SetWindowIconCommand(window.Id, "a.png")));
        Assert.Single(runtime.Apply(new SetWindowIconCommand(window.Id, null)));
        Assert.Null(Assert.Single(runtime.Windows).IconAssetPath);
    }

    private static WindowRuntimeState CreateState(string? icon) =>
        new(
            WindowId.FromGuid(Guid.NewGuid()),
            "org.hackeros.test",
            WindowOwnerId.FromGuid(Guid.NewGuid()),
            "Test",
            icon,
            new WindowBounds(20, 30, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 1200, 900));
}
