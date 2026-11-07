using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Creates the <see cref="IAppWindowGateway"/> each rendered window app receives (ADR 0117 §2-3):
/// one per window, bound to that window's id, so an app only ever renames its own window.
/// </summary>
public sealed class AppWindowGatewayFactory
{
    private readonly WindowRuntime _runtime;
    private readonly Func<AppManifest, string> _defaultTitle;

    /// <summary>Creates a factory whose default title is the manifest's own name.</summary>
    /// <param name="runtime">The desktop window runtime.</param>
    public AppWindowGatewayFactory(WindowRuntime runtime)
        : this(runtime, static manifest => manifest.Name)
    {
    }

    /// <summary>
    /// Creates a factory whose default title is the one a new window gets: the app's name in the
    /// desktop language (ADR 0104 §4), as <see cref="WindowLaunchCoordinator"/> names it.
    /// </summary>
    /// <param name="runtime">The desktop window runtime.</param>
    /// <param name="launchCoordinator">Gives the localized default title.</param>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AppWindowGatewayFactory(WindowRuntime runtime, WindowLaunchCoordinator launchCoordinator)
        : this(runtime, (launchCoordinator ?? throw new ArgumentNullException(nameof(launchCoordinator))).DefaultTitle)
    {
    }

    private AppWindowGatewayFactory(WindowRuntime runtime, Func<AppManifest, string> defaultTitle)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _defaultTitle = defaultTitle;
    }

    /// <summary>Creates the gateway of one window.</summary>
    /// <param name="windowId">The app's window.</param>
    /// <param name="manifest">The app's manifest, for the default title.</param>
    public IAppWindowGateway Create(WindowId windowId, AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new Gateway(_runtime, windowId, () => _defaultTitle(manifest), manifest.Window ?? WindowOptions.Default);
    }

    /// <summary>
    /// One window's gateway: reads and writes that window's title, frame and geometry in the runtime
    /// (ADR 0117 §2, ADR 0234 §4). Every request reaches this window only.
    /// </summary>
    private sealed class Gateway(WindowRuntime runtime, WindowId windowId, Func<string> defaultTitle, WindowOptions options) : IAppWindowGateway
    {
        // Last title shown, kept for reads once the window has left the runtime (closing).
        private string? _lastTitle;
        private WindowOptions _options = options;

        public WindowOptions Options => _options;

        public string Title => Find()?.Title ?? _lastTitle ?? DefaultTitle;

        public string DefaultTitle => WindowTitles.Sanitize(defaultTitle());

        public ValueTask<string> SetTitleAsync(string? title, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string shown = WindowTitles.SanitizeOrDefault(title, DefaultTitle);
            if (shown.Length == 0)
            {
                return ValueTask.FromResult(Title);
            }

            // A window already closed keeps no title: the call is a no-op, as for an X11 client
            // writing a property on a destroyed window.
            if (Find() is not null)
            {
                runtime.SetTitle(windowId, shown);
            }

            _lastTitle = shown;
            return ValueTask.FromResult(shown);
        }

        public ValueTask<bool> SetOptionsAsync(WindowOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();
            bool applied = TryApply(new SetWindowFrameCommand(
                windowId,
                WindowLaunchCoordinator.ToFrame(options),
                WindowLaunchCoordinator.ToConstraints(options)));
            if (applied)
            {
                _options = options;
            }

            return ValueTask.FromResult(applied);
        }

        public ValueTask<bool> MinimizeAsync(CancellationToken cancellationToken = default) =>
            ApplyState(new MinimizeWindowCommand(windowId), WindowVisualState.Minimized, cancellationToken);

        public ValueTask<bool> MaximizeAsync(CancellationToken cancellationToken = default)
        {
            // A fixed-size window is never maximized (ADR 0095 §2), whoever asks.
            if (Find() is { Constraints.IsResizable: false })
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(false);
            }

            return ApplyState(new MaximizeWindowCommand(windowId), WindowVisualState.Maximized, cancellationToken);
        }

        public ValueTask<bool> RestoreAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Find() is not { } window)
            {
                return ValueTask.FromResult(false);
            }

            // Already in its normal geometry: nothing to restore, and the request is met.
            if (window.VisualState == WindowVisualState.Normal && !window.IsSnapped)
            {
                return ValueTask.FromResult(true);
            }

            return ValueTask.FromResult(TryApply(new RestoreWindowCommand(windowId)));
        }

        public ValueTask<bool> MoveAsync(double x, double y, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(x) || !double.IsFinite(y) || Find() is not { } window)
            {
                return ValueTask.FromResult(false);
            }

            // The app speaks in work-area coordinates; the runtime keeps the window on its screen.
            WindowBounds area = runtime.GetWorkArea(window.DisplayId);
            return ValueTask.FromResult(TryApply(new MoveWindowCommand(windowId, area.X + x, area.Y + y)));
        }

        public ValueTask<bool> ResizeAsync(double width, double height, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0
                || Find() is not { } window)
            {
                return ValueTask.FromResult(false);
            }

            WindowBounds bounds = new(window.Bounds.X, window.Bounds.Y, width, height);
            // A resize from the app ends at once, like a drag released: observers that save the
            // geometry (ADR 0116) see the final size.
            bool resized = TryApply(new ResizeWindowCommand(windowId, bounds))
                && TryApply(new CompleteWindowResizeCommand(windowId));
            return ValueTask.FromResult(resized);
        }

        private ValueTask<bool> ApplyState(WindowCommand command, WindowVisualState target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Find() is not { } window)
            {
                return ValueTask.FromResult(false);
            }

            return ValueTask.FromResult(window.VisualState == target || TryApply(command));
        }

        /// <summary>
        /// Applies one command to this window. The runtime refuses what the window's state forbids
        /// (a fixed-size window maximized, a minimized window moved, a closed window): the app gets
        /// <see langword="false"/> instead of an exception, as a Win32 call that fails.
        /// </summary>
        private bool TryApply(WindowCommand command)
        {
            if (Find() is null)
            {
                return false;
            }

            try
            {
                runtime.Apply(command);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }

        private WindowRuntimeState? Find() => runtime.Windows.FirstOrDefault(window => window.Id == windowId);
    }
}
