namespace HackerOs.Taskbar.Blazor;

/// <summary>
/// Decides which command a taskbar window-item click maps to, kept as a pure, testable step
/// separate from Taskbar.razor's rendering.
/// </summary>
internal static class TaskbarWindowInteraction
{
    /// <summary>
    /// Restores and activates a minimized window, minimizes a focused window, or otherwise
    /// activates a background window -- matching how a native taskbar toggles its items.
    /// </summary>
    public static void HandleClick(TaskbarWindowItem window, ITaskbarCommandDispatcher commands)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(commands);

        if (window.IsMinimized)
        {
            commands.Restore(window.Id);
            commands.Activate(window.Id);
        }
        else if (window.IsFocused)
        {
            commands.Minimize(window.Id);
        }
        else
        {
            commands.Activate(window.Id);
        }
    }

    /// <summary>
    /// Brings the window of a clicked preview to the front (ADR 0199 §3): restores it first when it is
    /// minimized. Unlike the button, a preview never minimizes its window.
    /// </summary>
    public static void Show(TaskbarWindowItem window, ITaskbarCommandDispatcher commands)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(commands);

        if (window.IsMinimized)
        {
            commands.Restore(window.Id);
        }

        commands.Activate(window.Id);
    }
}
