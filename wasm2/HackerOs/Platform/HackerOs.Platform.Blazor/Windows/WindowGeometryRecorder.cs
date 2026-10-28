using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>
/// Remembers the geometry of app windows (ADR 0116 §3), the way a window manager such as KWin or
/// Mutter keeps per-application placement: it listens to the facts of <see cref="WindowRuntime"/>,
/// keeps each tracked window's last <em>normal</em> bounds and maximized state in memory, and writes
/// them to <c>~/.local/state/hackeros/windows/&lt;appId&gt;.config</c> through
/// <see cref="WindowGeometryPersistence"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only windows registered with <see cref="Track"/> are remembered: <see cref="WindowLaunchCoordinator"/>
/// registers app windows whose <c>WindowOptions.RememberGeometry</c> is true. File dialogs, the
/// authentication agent and other OS windows are never tracked.
/// </para>
/// <para>
/// Resize samples can arrive throughout a gesture; writing each would hammer the virtual disk.
/// Size changes are grouped per app and written after <see cref="DefaultDebounce"/> of quiet. A move
/// alone is never persisted (ADR 0122). Closing a
/// window writes at once, since the window will produce no further fact.
/// </para>
/// <para>
/// Snapping (keyboard <c>snap-left</c>/<c>snap-right</c>) leaves a normal window covering one half of
/// the work area, marked <see cref="WindowRuntimeState.IsSnapped"/> (ADR 0177). Such a half-tile is not
/// recorded: the window keeps the geometry it had before the snap and reopens as a normal window.
/// </para>
/// </remarks>
public sealed class WindowGeometryRecorder : IDisposable
{
    /// <summary>Quiet time after the last change of an app's window before its geometry is written.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly WindowRuntime _runtime;
    private readonly WindowGeometryPersistence _persistence;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly Dictionary<WindowId, TrackedWindow> _tracked = [];
    private readonly Dictionary<string, PendingSave> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _writes = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _running = [];
    private bool _disposed;

    /// <summary>Creates a recorder over the desktop's window runtime with the default 500 ms grouping.</summary>
    /// <param name="runtime">Authoritative window runtime.</param>
    /// <param name="persistence">State file reader and writer.</param>
    public WindowGeometryRecorder(WindowRuntime runtime, WindowGeometryPersistence persistence)
        : this(runtime, persistence, DefaultDebounce)
    {
    }

    /// <summary>Creates a recorder with an explicit grouping delay (tests use <see cref="TimeSpan.Zero"/>).</summary>
    /// <param name="runtime">Authoritative window runtime.</param>
    /// <param name="persistence">State file reader and writer.</param>
    /// <param name="debounce">Quiet time before a change is written.</param>
    public WindowGeometryRecorder(WindowRuntime runtime, WindowGeometryPersistence persistence, TimeSpan debounce)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        ArgumentOutOfRangeException.ThrowIfLessThan(debounce, TimeSpan.Zero);
        _debounce = debounce;
        _runtime.EventsApplied += OnEventsApplied;
    }

    /// <summary>Reads the geometry saved for <paramref name="appId"/>, or <see langword="null"/>.</summary>
    /// <param name="appId">App whose window is about to open.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// A window reopened right after it was closed, or while a grouped write still waits for quiet,
    /// must see that write: a waiting write of the app is started at once and every write of the app
    /// is awaited before the file is read.
    /// </remarks>
    public async ValueTask<SavedWindowGeometry?> ReadAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        Task? write;
        lock (_gate)
        {
            if (_pending.Remove(appId, out PendingSave? pending))
            {
                pending.Cancellation.Cancel();
                StartWrite(appId, pending.Geometry);
            }

            write = _writes.GetValueOrDefault(appId);
        }

        if (write is not null)
        {
            await write.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _persistence.RestoreAsync(appId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts remembering the geometry of <paramref name="windowId"/> under <paramref name="appId"/>.
    /// The window's current state is the starting point; nothing is written until it changes or closes.
    /// </summary>
    /// <param name="windowId">An open window of the runtime.</param>
    /// <param name="appId">App whose state file receives the geometry.</param>
    public void Track(WindowId windowId, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        WindowRuntimeState? window = _runtime.Windows.FirstOrDefault(candidate => candidate.Id == windowId);
        if (window is null)
        {
            return;
        }

        lock (_gate)
        {
            TrackedWindow tracked = new(appId);
            Capture(tracked, window);
            _tracked[windowId] = tracked;
        }
    }

    /// <summary>Waits until every geometry write scheduled so far has finished (tests, shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_gate)
            {
                running = [.. _running];
            }

            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runtime.EventsApplied -= OnEventsApplied;
        lock (_gate)
        {
            _disposed = true;
            foreach (PendingSave pending in _pending.Values)
            {
                pending.Cancellation.Cancel();
            }

            _pending.Clear();
            _tracked.Clear();
        }
    }

    private void OnEventsApplied(IReadOnlyList<WindowEvent> events)
    {
        lock (_gate)
        {
            if (_disposed || _tracked.Count == 0)
            {
                return;
            }

            foreach (WindowEvent windowEvent in events)
            {
                if (windowEvent is WindowClosedEvent closed && _tracked.Remove(closed.WindowId, out TrackedWindow? tracked))
                {
                    // The window is gone: write its last geometry now, replacing any grouped write.
                    Schedule(tracked.AppId, tracked.Snapshot(), TimeSpan.Zero);
                }
            }

            // Every other fact (move, resize, maximize, restore, work-area change) is read back from
            // the runtime's snapshots rather than from each event, so the recorder always sees the
            // constrained bounds the runtime actually applied.
            foreach (WindowRuntimeState window in _runtime.Windows)
            {
                if (_tracked.TryGetValue(window.Id, out TrackedWindow? tracked) && Capture(tracked, window))
                {
                    Schedule(tracked.AppId, tracked.Snapshot(), _debounce);
                }
            }
        }
    }

    /// <summary>Updates <paramref name="tracked"/> from a snapshot; returns whether its saved geometry changed.</summary>
    private static bool Capture(TrackedWindow tracked, WindowRuntimeState window)
    {
        WindowBounds? normal = tracked.NormalBounds;
        bool maximized = tracked.IsMaximized;
        switch (window.VisualState)
        {
            case WindowVisualState.Normal:
                maximized = false;
                if (!window.IsSnapped)
                {
                    normal = window.Bounds;
                }

                break;
            case WindowVisualState.Maximized:
                maximized = true;
                normal = window.RestoreBounds ?? normal;
                break;
            default:
                // Minimized: never saved (ADR 0116 §2); the window keeps what it had before.
                break;
        }

        // Only the size is saved (ADR 0122): a move alone changes nothing worth writing.
        if (normal is not WindowBounds next
            || (tracked.NormalBounds is WindowBounds previous
                && next.Width == previous.Width
                && next.Height == previous.Height
                && maximized == tracked.IsMaximized))
        {
            return false;
        }

        tracked.NormalBounds = normal;
        tracked.IsMaximized = maximized;
        return true;
    }

    /// <summary>Replaces the app's pending write with one that runs after <paramref name="delay"/>.</summary>
    private void Schedule(string appId, SavedWindowGeometry? geometry, TimeSpan delay)
    {
        if (geometry is null)
        {
            return;
        }

        if (_pending.Remove(appId, out PendingSave? previous))
        {
            previous.Cancellation.Cancel();
        }

        if (delay <= TimeSpan.Zero)
        {
            StartWrite(appId, geometry);
            return;
        }

        PendingSave pending = new(geometry, new CancellationTokenSource());
        _pending[appId] = pending;
        Observe(WriteAfterQuietAsync(appId, delay, pending));
    }

    /// <summary>Waits for quiet, then writes unless a newer change of the app replaced this one.</summary>
    private async Task WriteAfterQuietAsync(string appId, TimeSpan delay, PendingSave pending)
    {
        try
        {
            await Task.Delay(delay, pending.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (!_pending.TryGetValue(appId, out PendingSave? current) || !ReferenceEquals(current, pending))
                {
                    return;
                }

                _pending.Remove(appId);
                StartWrite(appId, pending.Geometry);
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer change of the same app, flushed by a read, or the desktop was disposed.
        }
        finally
        {
            pending.Cancellation.Dispose();
        }
    }

    /// <summary>
    /// Starts writing <paramref name="geometry"/> after the app's previous write, so two writes of one
    /// app never race and the file always ends with the newest geometry. Called under the gate.
    /// </summary>
    private void StartWrite(string appId, SavedWindowGeometry geometry)
    {
        Task previous = _writes.GetValueOrDefault(appId) ?? Task.CompletedTask;
        Task write = WriteAsync(previous, appId, geometry);
        _writes[appId] = write;
        Observe(write);
        _ = write.ContinueWith(
            finished =>
            {
                lock (_gate)
                {
                    if (_writes.TryGetValue(appId, out Task? last) && ReferenceEquals(last, finished))
                    {
                        _writes.Remove(appId);
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WriteAsync(Task previous, string appId, SavedWindowGeometry geometry)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The previous write's failure is already ignored by the persistence; keep the order going.
        }

        await _persistence.SaveAsync(appId, geometry).ConfigureAwait(false);
    }

    /// <summary>Keeps <paramref name="task"/> in the set awaited by <see cref="WhenIdleAsync"/> until it ends.</summary>
    private void Observe(Task task)
    {
        _running.Add(task);
        _ = task.ContinueWith(
            finished =>
            {
                lock (_gate)
                {
                    _running.Remove(finished);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Last geometry known for one tracked window.</summary>
    private sealed class TrackedWindow(string appId)
    {
        public string AppId { get; } = appId;

        public WindowBounds? NormalBounds { get; set; }

        public bool IsMaximized { get; set; }

        public SavedWindowGeometry? Snapshot() =>
            NormalBounds is WindowBounds bounds ? new SavedWindowGeometry(bounds.Width, bounds.Height, IsMaximized) : null;
    }

    /// <summary>One grouped write waiting for quiet.</summary>
    private sealed record PendingSave(SavedWindowGeometry Geometry, CancellationTokenSource Cancellation);
}
