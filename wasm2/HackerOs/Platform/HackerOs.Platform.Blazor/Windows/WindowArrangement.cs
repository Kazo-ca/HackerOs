using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>
/// The commands of the taskbar menu that act on every window of one screen (ADR 0194 §4-5):
/// « Afficher le bureau », « Fenêtres en cascade » and « Fenêtres en mosaïque ».
/// </summary>
internal static class WindowArrangement
{
    /// <summary>Number of cascade slots before the stairs start again from the corner.</summary>
    public const int CascadeSlots = 8;

    /// <summary>
    /// Gets the windows the commands act on, from back to front: the visible top-level windows of
    /// <paramref name="display"/> that no modal dialog blocks.
    /// </summary>
    /// <param name="runtime">The window runtime.</param>
    /// <param name="display">The screen of the taskbar.</param>
    public static IReadOnlyList<WindowRuntimeState> ArrangeableWindows(WindowRuntime runtime, DisplayId display)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return
        [
            .. runtime.Windows.Where(window =>
                window.DisplayId == display
                && window.VisualState != WindowVisualState.Minimized
                && window.OwnerId is null
                && window.Modality == WindowModality.Modeless
                && !runtime.IsInteractionBlocked(window.Id)),
        ];
    }

    /// <summary>« Afficher le bureau »: minimizes every arrangeable window of the screen.</summary>
    /// <param name="runtime">The window runtime.</param>
    /// <param name="display">The screen of the taskbar.</param>
    /// <returns>The number of windows minimized.</returns>
    public static int ShowDesktop(WindowRuntime runtime, DisplayId display)
    {
        IReadOnlyList<WindowRuntimeState> windows = ArrangeableWindows(runtime, display);
        foreach (WindowRuntimeState window in windows)
        {
            runtime.Apply(new MinimizeWindowCommand(window.Id));
        }

        return windows.Count;
    }

    /// <summary>
    /// « Fenêtres en cascade »: from back to front, each window keeps its size and steps down from
    /// the corner of the work area by <see cref="WindowLaunchCoordinator.CascadeStep"/>.
    /// </summary>
    /// <param name="runtime">The window runtime.</param>
    /// <param name="display">The screen of the taskbar.</param>
    /// <returns>The number of windows arranged.</returns>
    public static int Cascade(WindowRuntime runtime, DisplayId display) =>
        Arrange(runtime, display, static (area, index, _, window) =>
        {
            double offset = (index % CascadeSlots) * WindowLaunchCoordinator.CascadeStep;
            return new WindowBounds(area.X + offset, area.Y + offset, window.Bounds.Width, window.Bounds.Height);
        });

    /// <summary>
    /// « Fenêtres en mosaïque »: a grid of <c>ceil(√n)</c> columns covers the work area; a resizable
    /// window takes its cell, another one moves to its cell's corner.
    /// </summary>
    /// <param name="runtime">The window runtime.</param>
    /// <param name="display">The screen of the taskbar.</param>
    /// <returns>The number of windows arranged.</returns>
    public static int Tile(WindowRuntime runtime, DisplayId display) =>
        Arrange(runtime, display, static (area, index, count, window) =>
        {
            WindowBounds cell = TileCell(area, index, count);
            return window.Constraints.IsResizable
                ? cell
                : new WindowBounds(cell.X, cell.Y, window.Bounds.Width, window.Bounds.Height);
        });

    /// <summary>Gets the cell of window <paramref name="index"/> among <paramref name="count"/> in the tile grid.</summary>
    /// <param name="area">The work area.</param>
    /// <param name="index">Position of the window, from back to front.</param>
    /// <param name="count">Number of windows.</param>
    public static WindowBounds TileCell(WindowBounds area, int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        int columns = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling(count / (double)columns);
        double width = Math.Floor(area.Width / columns);
        double height = Math.Floor(area.Height / rows);
        return new WindowBounds(
            area.X + ((index % columns) * width),
            area.Y + ((index / columns) * height),
            width,
            height);
    }

    private static int Arrange(
        WindowRuntime runtime,
        DisplayId display,
        Func<WindowBounds, int, int, WindowRuntimeState, WindowBounds> place)
    {
        IReadOnlyList<WindowRuntimeState> windows = [.. ArrangeableWindows(runtime, display).Where(static window => window.Constraints.IsMovable)];
        if (windows.Count == 0)
        {
            return 0;
        }

        WindowBounds area = runtime.GetWorkArea(display);
        bool restacked = false;
        for (int index = 0; index < windows.Count; index++)
        {
            WindowId id = windows[index].Id;
            if (windows[index].VisualState == WindowVisualState.Maximized)
            {
                // Restoring a background window focuses it, which raises it: the order is put back below.
                restacked |= !windows[index].IsFocused;
                runtime.Apply(new RestoreWindowCommand(id));
            }

            WindowRuntimeState window = runtime.Windows.First(candidate => candidate.Id == id);
            WindowBounds target = place(area, index, windows.Count, window);
            // A move takes a snapped window off its tile (ADR 0177) before it gets its new size.
            runtime.Apply(new MoveWindowCommand(id, target.X, target.Y));
            if (window.Constraints.IsResizable && (target.Width != window.Bounds.Width || target.Height != window.Bounds.Height))
            {
                runtime.Apply(new ResizeWindowCommand(id, target));
                runtime.Apply(new CompleteWindowResizeCommand(id));
            }
        }

        if (restacked)
        {
            foreach (WindowRuntimeState window in windows)
            {
                runtime.Apply(new FocusWindowCommand(window.Id));
            }
        }

        return windows.Count;
    }
}
