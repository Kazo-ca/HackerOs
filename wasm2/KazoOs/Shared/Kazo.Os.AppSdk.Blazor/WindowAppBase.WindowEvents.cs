using Kazo.Os.AppSdk.Blazor.Display;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Window events of a window app (ADR 0095 §1): focus, minimize/maximize/restore and resize, as a
/// client of a real window manager receives them.
/// </summary>
public abstract partial class WindowAppBase
{
    private readonly HashSet<string> _activeSurfaces = new(StringComparer.Ordinal);
    private bool _desktopWindowActive;

    /// <summary>
    /// Gets whether the app has the keyboard focus: its window on the desktop is the active one, or one
    /// of the detached surfaces it follows (<see cref="TrackDisplaySurface"/>) has the focus.
    /// </summary>
    protected bool IsWindowActive { get; private set; }

    /// <summary>Gets the current state of the app's window. <see cref="HackWindowState.Normal"/> until told otherwise.</summary>
    protected HackWindowState WindowState { get; private set; } = HackWindowState.Normal;

    /// <summary>
    /// Gets the last client size the window manager reported, or <see langword="null"/> before the
    /// first resize (the initial size is the one the app was laid out in).
    /// </summary>
    protected HackWindowSize? WindowSize { get; private set; }

    /// <summary>
    /// Runs when the app gains the keyboard focus. Called once per transition, never twice in a row.
    /// </summary>
    protected virtual void OnWindowActivated()
    {
    }

    /// <summary>Asynchronous variant of <see cref="OnWindowActivated"/>, called right after it.</summary>
    protected virtual Task OnWindowActivatedAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs when the app loses the keyboard focus to another window. Called once per transition.
    /// </summary>
    protected virtual void OnWindowDeactivated()
    {
    }

    /// <summary>Asynchronous variant of <see cref="OnWindowDeactivated"/>, called right after it.</summary>
    protected virtual Task OnWindowDeactivatedAsync() => Task.CompletedTask;

    /// <summary>
    /// Runs after the window was minimized, maximized or restored. A minimized app keeps running, as
    /// under a real window manager: slowing its own work down while minimized is its choice, here.
    /// </summary>
    /// <param name="previous">State before the change.</param>
    /// <param name="current">State after the change, also in <see cref="WindowState"/>.</param>
    protected virtual void OnWindowStateChanged(HackWindowState previous, HackWindowState current)
    {
    }

    /// <summary>Asynchronous variant of <see cref="OnWindowStateChanged"/>, called right after it.</summary>
    /// <param name="previous">State before the change.</param>
    /// <param name="current">State after the change.</param>
    protected virtual Task OnWindowStateChangedAsync(HackWindowState previous, HackWindowState current) => Task.CompletedTask;

    /// <summary>
    /// Runs once a resize is over: when the pointer is released, a keyboard resize is validated, or
    /// the window is maximized or restored. Never once per pixel.
    /// </summary>
    /// <param name="size">New client size, also in <see cref="WindowSize"/>.</param>
    protected virtual void OnWindowResized(HackWindowSize size)
    {
    }

    /// <summary>Asynchronous variant of <see cref="OnWindowResized"/>, called right after it.</summary>
    /// <param name="size">New client size.</param>
    protected virtual Task OnWindowResizedAsync(HackWindowSize size) => Task.CompletedTask;

    /// <summary>
    /// Follows a detached display surface the app opened (ADR 0078, ADR 0081): the focus, blur and
    /// resize of the browser window that carries it call the same hooks as the desktop window. The app
    /// is active while either has the focus; <see cref="HackWindowSize.DisplaySurfaceId"/> tells a
    /// surface resize from a window resize.
    /// </summary>
    /// <param name="surface">The open surface.</param>
    /// <returns>Stops following the surface. Following also stops by itself when the surface ends.</returns>
    protected IDisposable TrackDisplaySurface(IDisplaySurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return new DisplaySurfaceTracking(this, surface);
    }

    /// <inheritdoc />
    Task IWindowAppEventTarget.NotifyActivatedAsync() =>
        RunWindowHookAsync(nameof(OnWindowActivated), () =>
        {
            _desktopWindowActive = true;
            return ApplyActivationAsync();
        });

    /// <inheritdoc />
    Task IWindowAppEventTarget.NotifyDeactivatedAsync() =>
        RunWindowHookAsync(nameof(OnWindowDeactivated), () =>
        {
            _desktopWindowActive = false;
            return ApplyActivationAsync();
        });

    /// <inheritdoc />
    Task IWindowAppEventTarget.NotifyStateChangedAsync(HackWindowState previous, HackWindowState current) =>
        RunWindowHookAsync(nameof(OnWindowStateChanged), async () =>
        {
            WindowState = current;
            OnWindowStateChanged(previous, current);
            await OnWindowStateChangedAsync(previous, current);
        });

    /// <inheritdoc />
    Task IWindowAppEventTarget.NotifyResizedAsync(HackWindowSize size) =>
        RunWindowHookAsync(nameof(OnWindowResized), async () =>
        {
            if (size.DisplaySurfaceId is null)
            {
                WindowSize = size;
            }

            OnWindowResized(size);
            await OnWindowResizedAsync(size);
        });

    /// <summary>
    /// Calls the activation hooks when the combined focus of the window and its tracked surfaces
    /// changes, so moving the focus from the window to one of its own surfaces is no transition.
    /// </summary>
    private async Task ApplyActivationAsync()
    {
        bool active = _desktopWindowActive || _activeSurfaces.Count > 0;
        if (active == IsWindowActive)
        {
            return;
        }

        IsWindowActive = active;
        if (active)
        {
            OnWindowActivated();
            await OnWindowActivatedAsync();
        }
        else
        {
            OnWindowDeactivated();
            await OnWindowDeactivatedAsync();
        }
    }

    /// <summary>
    /// Runs one hook on the app's render thread, inside its ambient scope (ADR 0043). A failing hook
    /// is written to <c>/var/log/apps/&lt;appId&gt;.log</c> and never stops the window transition: the
    /// window manager stays in charge of the window's state (ADR 0009).
    /// </summary>
    private Task RunWindowHookAsync(string hookName, Func<Task> hook)
    {
        async Task GuardedAsync()
        {
            using IDisposable? scope = AmbientComponentDispatch.Enter(AppContext);
            try
            {
                await hook();
            }
            catch (Exception exception)
            {
                LogWindowHookFailure(hookName, exception);
            }
        }

        try
        {
            return InvokeAsync(GuardedAsync);
        }
        catch (InvalidOperationException)
        {
            // Not attached to a renderer yet (never rendered): run in place.
            return GuardedAsync();
        }
    }

    private void LogWindowHookFailure(string hookName, Exception exception)
    {
        try
        {
            AppContext?.Logging.Log(
                DiagnosticSeverity.Error,
                $"window: {hookName} failed: {exception.GetType().Name}: {exception.Message}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["hook"] = hookName });
        }
        catch (Exception)
        {
            // Logging is best effort; a window event must never fail because of it.
        }
    }

    private Task SetSurfaceFocusAsync(string surfaceId, bool hasFocus) =>
        RunWindowHookAsync(hasFocus ? nameof(OnWindowActivated) : nameof(OnWindowDeactivated), () =>
        {
            bool changed = hasFocus ? _activeSurfaces.Add(surfaceId) : _activeSurfaces.Remove(surfaceId);
            return changed ? ApplyActivationAsync() : Task.CompletedTask;
        });

    /// <summary>Relays one detached surface's focus and size to the window hooks.</summary>
    private sealed class DisplaySurfaceTracking : IDisposable
    {
        private readonly WindowAppBase _owner;
        private readonly IDisplaySurface _surface;
        private bool _hasFocus;
        private int _width;
        private int _height;
        private bool _disposed;

        public DisplaySurfaceTracking(WindowAppBase owner, IDisplaySurface surface)
        {
            _owner = owner;
            _surface = surface;
            _width = surface.Width;
            _height = surface.Height;
            surface.Changed += OnChanged;
            _owner.OnSurfaceTrackingChanged(this, tracked: true);
            if (surface.State == DisplaySurfaceState.Open && surface.HasFocus)
            {
                _hasFocus = true;
                _ = _owner.SetSurfaceFocusAsync(surface.Id, hasFocus: true);
            }
        }

        private void OnChanged(IDisplaySurface surface)
        {
            if (surface.State != DisplaySurfaceState.Open)
            {
                Dispose();
                return;
            }

            if (surface.HasFocus != _hasFocus)
            {
                _hasFocus = surface.HasFocus;
                _ = _owner.SetSurfaceFocusAsync(surface.Id, _hasFocus);
            }

            if (surface.Width != _width || surface.Height != _height)
            {
                _width = surface.Width;
                _height = surface.Height;
                _ = ((IWindowAppEventTarget)_owner).NotifyResizedAsync(
                    new HackWindowSize(_width, _height) { DisplaySurfaceId = surface.Id });
            }
        }

        /// <summary>
        /// Gives the surface's browser window the app's title (ADR 0117 §3). A surface that just
        /// closed or a browser that refuses is logged, never thrown at the app.
        /// </summary>
        public async Task ApplyTitleAsync(string title)
        {
            if (_disposed || _surface.State != DisplaySurfaceState.Open)
            {
                return;
            }

            try
            {
                await _surface.SetTitleAsync(title);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _owner.LogSurfaceTitleFailure(exception);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _surface.Changed -= OnChanged;
            _owner.OnSurfaceTrackingChanged(this, tracked: false);
            if (_hasFocus)
            {
                _hasFocus = false;
                _ = _owner.SetSurfaceFocusAsync(_surface.Id, hasFocus: false);
            }
        }
    }
}
