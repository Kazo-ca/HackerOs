using HackerOs.Taskbar.Blazor;
using HackerOs.Windowing.Abstractions;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The desktop's window previews (ADR 0199): every taskbar copies a window from its element in the
/// window layer (<c>data-window-id</c>, set by <c>WindowHost</c>), and the peek goes to the desktop
/// area, which shows the peeked window alone on its screen. One instance serves the taskbars of
/// every screen: a single window can be peeked at a time.
/// </summary>
internal sealed class TaskbarWindowPreviews : ITaskbarWindowPreviews
{
    /// <summary>Gets the window the user peeks at, or <see langword="null"/>.</summary>
    public WindowId? PeekWindowId { get; private set; }

    /// <summary>Raised when <see cref="PeekWindowId"/> changes, to render the desktop area again.</summary>
    public event Action? Changed;

    /// <inheritdoc />
    public string? LiveCopySelector(WindowId id) => $"[data-window-id=\"{id}\"]";

    /// <inheritdoc />
    public void Peek(WindowId? id)
    {
        if (PeekWindowId != id)
        {
            PeekWindowId = id;
            Changed?.Invoke();
        }
    }
}
