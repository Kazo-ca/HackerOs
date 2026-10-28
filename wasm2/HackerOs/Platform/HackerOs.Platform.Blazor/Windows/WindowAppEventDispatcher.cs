using HackerOs.AppSdk.Blazor;
using HackerOs.Windowing.Abstractions;
using HackerOs.Windowing.Core;

namespace HackerOs.Platform.Blazor.Windows;

/// <summary>
/// Measures the client area of a desktop window (ADR 0095 §1): the size an app actually draws in,
/// once the window's new geometry has been laid out.
/// </summary>
public interface IWindowClientAreaProbe
{
    /// <summary>
    /// Measures the client area of <paramref name="windowId"/>, or returns <see langword="null"/> when
    /// it cannot (no browser, window not on screen). Waits for the next layout first.
    /// </summary>
    /// <param name="windowId">Window to measure.</param>
    ValueTask<HackWindowSize?> MeasureAsync(WindowId windowId);
}

/// <summary>
/// Turns the window manager's facts into the <c>OnWindow*</c> hooks of window apps (ADR 0095 §1),
/// in the order <see cref="WindowRuntime"/> produced them, one queue per window.
/// </summary>
/// <remarks>
/// <para>
/// Focus follows <see cref="WindowFocusedEvent"/>: the previous window is deactivated, then the new
/// one activated, like an X11 <c>FocusOut</c>/<c>FocusIn</c> pair. A minimized active window with no
/// other window to take the focus is deactivated after its state change.
/// </para>
/// <para>
/// A resize is reported once it is over: at <see cref="WindowResizeCompletedEvent"/> (pointer released
/// or keyboard resize validated), after a maximize or restore, and when a work-area change resized the
/// window. Intermediate <see cref="WindowResizedEvent"/>s of a gesture are not reported.
/// </para>
/// </remarks>
public sealed class WindowAppEventDispatcher : IDisposable
{
    /// <summary>Height of the default title bar (<c>--hos-titlebar-height</c>, 2.35rem) in CSS pixels.</summary>
    private const double EstimatedTitleBarHeight = 37.6;

    /// <summary>Border of the window frame plus the title bar's bottom border, in CSS pixels.</summary>
    private const double EstimatedFrameThickness = 1;

    private readonly WindowRuntime _runtime;
    private readonly IWindowClientAreaProbe? _probe;
    private readonly Dictionary<WindowId, Registration> _registrations = [];

    /// <summary>Creates a dispatcher over the desktop's window runtime.</summary>
    /// <param name="runtime">Authoritative window runtime.</param>
    /// <param name="probe">
    /// Optional client-area measurement; without it (tests, no browser), the client size is
    /// estimated from the window's outer size and the default chrome.
    /// </param>
    public WindowAppEventDispatcher(WindowRuntime runtime, IWindowClientAreaProbe? probe = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _probe = probe;
        _runtime.EventsApplied += OnEventsApplied;
    }

    /// <summary>
    /// Connects a rendered window app to its window. The app is told at once if its window already
    /// has the focus or is not in the normal state.
    /// </summary>
    /// <param name="windowId">The app's window.</param>
    /// <param name="target">The app component (a <see cref="WindowAppBase"/>).</param>
    /// <returns>Disconnects the app; called when its window content is disposed.</returns>
    public IDisposable Register(WindowId windowId, IWindowAppEventTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        WindowRuntimeState? window = _runtime.Windows.FirstOrDefault(candidate => candidate.Id == windowId);
        Registration registration = new(this, windowId, target)
        {
            State = window?.VisualState ?? WindowVisualState.Normal,
            OuterSize = window is null ? default : (window.Bounds.Width, window.Bounds.Height),
        };
        _registrations[windowId] = registration;

        if (registration.State != WindowVisualState.Normal)
        {
            WindowVisualState state = registration.State;
            registration.Enqueue(() => target.NotifyStateChangedAsync(HackWindowState.Normal, ToSdk(state)));
        }

        if (window?.IsFocused == true)
        {
            registration.IsActive = true;
            registration.Enqueue(target.NotifyActivatedAsync);
        }

        return registration;
    }

    /// <summary>Gets the client size estimated from outer window bounds and the default chrome.</summary>
    /// <param name="bounds">Outer window bounds.</param>
    public static HackWindowSize EstimateClientSize(WindowBounds bounds) => new(
        Math.Max(0, bounds.Width - (2 * EstimatedFrameThickness)),
        Math.Max(0, bounds.Height - EstimatedTitleBarHeight - (3 * EstimatedFrameThickness)));

    /// <summary>Waits until every notification queued so far has been delivered (tests, shutdown).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_registrations.Values.Select(registration => registration.Tail));

    /// <inheritdoc />
    public void Dispose()
    {
        _runtime.EventsApplied -= OnEventsApplied;
        _registrations.Clear();
    }

    private void OnEventsApplied(IReadOnlyList<WindowEvent> events)
    {
        foreach (WindowEvent windowEvent in events)
        {
            switch (windowEvent)
            {
                case WindowFocusedEvent focused:
                    if (focused.PreviousWindowId is WindowId previous)
                    {
                        Deactivate(previous);
                    }

                    Activate(focused.WindowId);
                    break;
                case WindowVisualStateChangedEvent changed when _registrations.TryGetValue(changed.WindowId, out Registration? registration):
                    WindowVisualState from = registration.State;
                    registration.State = changed.State;
                    registration.Enqueue(() => registration.Target.NotifyStateChangedAsync(ToSdk(from), ToSdk(changed.State)));
                    if (changed.State != WindowVisualState.Minimized)
                    {
                        ReportSizeIfChanged(registration, changed.Bounds);
                    }

                    break;
                case WindowResizeCompletedEvent completed when _registrations.TryGetValue(completed.WindowId, out Registration? registration):
                    ReportSizeIfChanged(registration, completed.Bounds);
                    break;
                case WindowDisplayChangedEvent moved when _registrations.TryGetValue(moved.WindowId, out Registration? relocated):
                    // A window that changed screen may have taken another size (ADR 0178 §2).
                    if (relocated.State != WindowVisualState.Minimized)
                    {
                        ReportSizeIfChanged(relocated, moved.Bounds);
                    }

                    break;
                case WindowViewportChangedEvent viewport:
                    foreach (WindowRuntimeState window in viewport.Windows)
                    {
                        if (window.VisualState != WindowVisualState.Minimized
                            && _registrations.TryGetValue(window.Id, out Registration? resized))
                        {
                            ReportSizeIfChanged(resized, window.Bounds);
                        }
                    }

                    break;
                case WindowClosedEvent closed:
                    _registrations.Remove(closed.WindowId);
                    break;
            }
        }

        // Minimizing the last visible window leaves no window focused and produces no focus fact.
        WindowId? focusedId = _runtime.Windows.FirstOrDefault(window => window.IsFocused)?.Id;
        foreach (Registration registration in _registrations.Values.Where(item => item.IsActive && item.WindowId != focusedId).ToArray())
        {
            Deactivate(registration.WindowId);
        }
    }

    private void Activate(WindowId windowId)
    {
        if (_registrations.TryGetValue(windowId, out Registration? registration) && !registration.IsActive)
        {
            registration.IsActive = true;
            registration.Enqueue(registration.Target.NotifyActivatedAsync);
        }
    }

    private void Deactivate(WindowId windowId)
    {
        if (_registrations.TryGetValue(windowId, out Registration? registration) && registration.IsActive)
        {
            registration.IsActive = false;
            registration.Enqueue(registration.Target.NotifyDeactivatedAsync);
        }
    }

    private void ReportSizeIfChanged(Registration registration, WindowBounds bounds)
    {
        (double Width, double Height) outer = (bounds.Width, bounds.Height);
        if (outer == registration.OuterSize)
        {
            return;
        }

        registration.OuterSize = outer;
        WindowId windowId = registration.WindowId;
        registration.Enqueue(async () =>
        {
            HackWindowSize size = await MeasureAsync(windowId, bounds);
            await registration.Target.NotifyResizedAsync(size);
        });
    }

    private async Task<HackWindowSize> MeasureAsync(WindowId windowId, WindowBounds bounds)
    {
        if (_probe is not null)
        {
            try
            {
                if (await _probe.MeasureAsync(windowId) is { } measured)
                {
                    return measured;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Measurement is a refinement: fall back to the estimate below.
            }
        }

        return EstimateClientSize(bounds);
    }

    private static HackWindowState ToSdk(WindowVisualState state) => state switch
    {
        WindowVisualState.Minimized => HackWindowState.Minimized,
        WindowVisualState.Maximized => HackWindowState.Maximized,
        _ => HackWindowState.Normal,
    };

    /// <summary>One connected app and the ordered queue of its notifications.</summary>
    private sealed class Registration(WindowAppEventDispatcher owner, WindowId windowId, IWindowAppEventTarget target) : IDisposable
    {
        public WindowId WindowId { get; } = windowId;

        public IWindowAppEventTarget Target { get; } = target;

        public WindowVisualState State { get; set; }

        public (double Width, double Height) OuterSize { get; set; }

        public bool IsActive { get; set; }

        public Task Tail { get; private set; } = Task.CompletedTask;

        /// <summary>Runs <paramref name="work"/> after every earlier notification of this window.</summary>
        public void Enqueue(Func<Task> work) => Tail = RunAfterAsync(Tail, work);

        private static async Task RunAfterAsync(Task previous, Func<Task> work)
        {
            try
            {
                await previous;
            }
            catch (Exception)
            {
                // An earlier notification's failure is the app's (already logged); keep the order going.
            }

            await work();
        }

        public void Dispose()
        {
            if (owner._registrations.TryGetValue(WindowId, out Registration? current) && ReferenceEquals(current, this))
            {
                owner._registrations.Remove(WindowId);
            }
        }
    }
}
