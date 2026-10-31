using HackerOs.Windowing.Abstractions;

namespace HackerOs.Taskbar.Blazor;

/// <summary>
/// Decides when the hover preview of a window button opens, moves, closes and peeks (ADR 0199 §2),
/// kept as a pure, testable step separate from Taskbar.razor's rendering. Every call happens on the
/// renderer's dispatcher; the delays are scheduled through a host-supplied callback that comes back
/// there too.
/// </summary>
internal sealed class TaskbarPreviewController : IDisposable
{
    /// <summary>How long the pointer rests on a window button before its preview opens.</summary>
    public static readonly TimeSpan OpenDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>How long the preview stays after the pointer left it or its button, so the pointer can cross the gap.</summary>
    public static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the pointer rests on the preview before the other windows turn see-through.</summary>
    public static readonly TimeSpan PeekDelay = TimeSpan.FromMilliseconds(300);

    private readonly ITaskbarWindowPreviews _previews;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Action _changed;
    private IDisposable? _pending;
    private WindowId? _peeked;

    /// <summary>Creates a controller for one taskbar.</summary>
    /// <param name="previews">The host's previews, told when the peek starts and ends.</param>
    /// <param name="schedule">Runs an action once after a delay, on the dispatcher; disposing the result cancels it.</param>
    /// <param name="changed">Raised when <see cref="Shown"/> changes, to render the taskbar again.</param>
    public TaskbarPreviewController(ITaskbarWindowPreviews previews, Func<TimeSpan, Action, IDisposable> schedule, Action changed)
    {
        _previews = previews ?? throw new ArgumentNullException(nameof(previews));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
    }

    /// <summary>Gets the window whose preview is open, or <see langword="null"/>.</summary>
    public WindowId? Shown { get; private set; }

    /// <summary>
    /// The pointer entered a window button. A touch pointer never opens a preview (no hover, ADR 0199
    /// §2). When a preview is already open, it moves to this button at once, as on Windows.
    /// </summary>
    public void EnterItem(WindowId id, string? pointerType)
    {
        if (string.Equals(pointerType, "touch", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CancelPending();
        if (Shown is not null)
        {
            EndPeek();
            SetShown(id);
            return;
        }

        _pending = _schedule(OpenDelay, () => SetShown(id));
    }

    /// <summary>The pointer left a window button: the preview closes unless the pointer reaches it in time.</summary>
    public void LeaveItem() => ScheduleHide();

    /// <summary>The pointer entered the open preview: it stays, and the peek starts after a short rest.</summary>
    public void EnterPreview()
    {
        CancelPending();
        if (Shown is { } id)
        {
            _pending = _schedule(PeekDelay, () => StartPeek(id));
        }
    }

    /// <summary>The pointer left the preview: the peek ends and the preview closes unless the pointer comes back.</summary>
    public void LeavePreview()
    {
        EndPeek();
        ScheduleHide();
    }

    /// <summary>Closes the preview at once: a click on a button or on the preview, or its close button.</summary>
    public void Dismiss()
    {
        CancelPending();
        EndPeek();
        SetShown(null);
    }

    /// <summary>Closes the preview when its window left this taskbar (closed, or moved to another screen).</summary>
    public void Sync(IEnumerable<TaskbarWindowItem> windows)
    {
        if (Shown is { } id && !windows.Any(window => window.Id == id))
        {
            Dismiss();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancelPending();
        EndPeek();
    }

    private void ScheduleHide()
    {
        CancelPending();
        _pending = _schedule(HideDelay, () =>
        {
            EndPeek();
            SetShown(null);
        });
    }

    private void StartPeek(WindowId id)
    {
        _peeked = id;
        _previews.Peek(id);
    }

    private void EndPeek()
    {
        if (_peeked is not null)
        {
            _peeked = null;
            _previews.Peek(null);
        }
    }

    private void SetShown(WindowId? id)
    {
        if (Shown != id)
        {
            Shown = id;
            _changed();
        }
    }

    private void CancelPending()
    {
        _pending?.Dispose();
        _pending = null;
    }
}
