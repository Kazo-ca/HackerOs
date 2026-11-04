using Kazo.Os.Taskbar.Blazor;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Taskbar.Blazor.Tests;

public sealed class TaskbarWindowInteractionTests
{
    [Fact]
    public void Minimized_window_click_restores_then_activates()
    {
        RecordingCommands commands = new();
        TaskbarWindowItem window = CreateItem(isFocused: false, isMinimized: true);

        TaskbarWindowInteraction.HandleClick(window, commands);

        Assert.Equal([("Restore", window.Id), ("Activate", window.Id)], commands.Calls);
    }

    [Fact]
    public void Focused_window_click_minimizes()
    {
        RecordingCommands commands = new();
        TaskbarWindowItem window = CreateItem(isFocused: true, isMinimized: false);

        TaskbarWindowInteraction.HandleClick(window, commands);

        Assert.Equal([("Minimize", window.Id)], commands.Calls);
    }

    [Fact]
    public void Background_window_click_activates()
    {
        RecordingCommands commands = new();
        TaskbarWindowItem window = CreateItem(isFocused: false, isMinimized: false);

        TaskbarWindowInteraction.HandleClick(window, commands);

        Assert.Equal([("Activate", window.Id)], commands.Calls);
    }

    [Fact]
    public void Null_window_or_commands_throws()
    {
        RecordingCommands commands = new();
        TaskbarWindowItem window = CreateItem(isFocused: false, isMinimized: false);

        Assert.Throws<ArgumentNullException>(() => TaskbarWindowInteraction.HandleClick(null!, commands));
        Assert.Throws<ArgumentNullException>(() => TaskbarWindowInteraction.HandleClick(window, null!));
    }

    private static TaskbarWindowItem CreateItem(bool isFocused, bool isMinimized) => new(
        WindowId.FromGuid(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        "Test Window",
        IconAssetPath: null,
        isFocused,
        isMinimized);

    private sealed class RecordingCommands : ITaskbarCommandDispatcher
    {
        public List<(string Method, WindowId Id)> Calls { get; } = [];

        public void Activate(WindowId id) => Calls.Add(("Activate", id));
        public void Minimize(WindowId id) => Calls.Add(("Minimize", id));
        public void Restore(WindowId id) => Calls.Add(("Restore", id));
        public void Close(WindowId id) => Calls.Add(("Close", id));
    }
}
