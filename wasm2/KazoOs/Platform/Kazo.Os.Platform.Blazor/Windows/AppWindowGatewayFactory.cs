using System.Diagnostics.CodeAnalysis;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Kazo.Os.Platform.Blazor.ContextMenus;
using Kazo.Os.Windowing.Abstractions;
using Kazo.Os.Windowing.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>
/// Creates the <see cref="IAppWindowGateway"/> each rendered window app receives (ADR 0117 §2-3):
/// one per window, bound to that window's id, so an app only ever renames its own window.
/// </summary>
public sealed class AppWindowGatewayFactory
{
    private readonly WindowRuntime _runtime;
    private readonly Func<AppManifest, string> _defaultTitle;
    private readonly ContextMenuService? _menus;

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
    public AppWindowGatewayFactory(WindowRuntime runtime, WindowLaunchCoordinator launchCoordinator)
        : this(runtime, (launchCoordinator ?? throw new ArgumentNullException(nameof(launchCoordinator))).DefaultTitle, menus: null)
    {
    }

    /// <summary>
    /// Creates the desktop's factory: localized default titles, and context menus for the child
    /// windows an app opens (ADR 0234 §3, ADR 0150 §4).
    /// </summary>
    /// <param name="runtime">The desktop window runtime.</param>
    /// <param name="launchCoordinator">Gives the localized default title.</param>
    /// <param name="services">Gives the optional <see cref="ContextMenuService"/>.</param>
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AppWindowGatewayFactory(WindowRuntime runtime, WindowLaunchCoordinator launchCoordinator, IServiceProvider services)
        : this(
            runtime,
            (launchCoordinator ?? throw new ArgumentNullException(nameof(launchCoordinator))).DefaultTitle,
            (services ?? throw new ArgumentNullException(nameof(services))).GetService(typeof(ContextMenuService)) as ContextMenuService)
    {
    }

    private AppWindowGatewayFactory(WindowRuntime runtime, Func<AppManifest, string> defaultTitle, ContextMenuService? menus = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _defaultTitle = defaultTitle;
        _menus = menus;
    }

    /// <summary>Creates the gateway of one window.</summary>
    /// <param name="windowId">The app's window.</param>
    /// <param name="manifest">The app's manifest, for the default title.</param>
    public IAppWindowGateway Create(WindowId windowId, AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new Gateway(this, windowId, () => _defaultTitle(manifest), manifest.Window ?? WindowOptions.Default, context: null);
    }

    /// <summary>
    /// Creates the gateway of an app's window that can also open child windows (ADR 0234 §3): they
    /// render inside <paramref name="context"/>, the app instance's own execution context.
    /// </summary>
    /// <param name="windowId">The app's window.</param>
    /// <param name="context">The app instance's execution context.</param>
    public IAppWindowGateway Create(WindowId windowId, IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        AppManifest manifest = context.Manifest;
        return new Gateway(this, windowId, () => _defaultTitle(manifest), manifest.Window ?? WindowOptions.Default, context);
    }

    /// <summary>
    /// Opens a child window of <paramref name="owner"/> (ADR 0234 §3): same app instance, same
    /// process, centered on its owner, modal to it when the request asks.
    /// </summary>
    private ChildWindow OpenChild(
        WindowRuntimeState owner,
        IAppExecutionContext context,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        KazoWindowRequest request)
    {
        WindowOptions options = request.Options ?? WindowOptions.Default;
        WindowId childId = WindowId.FromGuid(Guid.NewGuid());
        string defaultTitle = WindowTitles.Sanitize(_defaultTitle(context.Manifest));
        string title = WindowTitles.SanitizeOrDefault(request.Title, defaultTitle);
        ContextMenuScope? menus = _menus?.CreateScope(context);
        Gateway childGateway = new(this, childId, () => defaultTitle, options, context);
        ChildWindow child = new(_runtime, childId, owner.Id, menus);
        IReadOnlyDictionary<string, object?> parameters = request.Parameters;

        void RenderComponent(RenderTreeBuilder builder)
        {
            builder.OpenComponent(0, componentType);
            int sequence = 1;
            foreach ((string name, object? value) in parameters)
            {
                builder.AddComponentParameter(sequence++, name, value);
            }

            builder.CloseComponent();
        }

        RenderFragment content = builder => Cascade<IAppExecutionContext>(builder, context, inner =>
        {
            RenderFragment window = withMenus => Cascade<IAppWindowGateway>(withMenus, childGateway, withGateway =>
                Cascade<IKazoWindow>(withGateway, child, RenderComponent));
            if (menus is null)
            {
                window(inner);
            }
            else
            {
                Cascade<IKazoMenuHost>(inner, menus, window);
            }
        });

        WindowBounds area = _runtime.GetWorkArea(owner.DisplayId);
        WindowBounds size = WindowLaunchCoordinator.InitialBounds(options, 0, area);
        double x = Math.Clamp(owner.Bounds.X + ((owner.Bounds.Width - size.Width) / 2), area.X, Math.Max(area.X, area.X + area.Width - size.Width));
        double y = Math.Clamp(owner.Bounds.Y + ((owner.Bounds.Height - size.Height) / 2), area.Y, Math.Max(area.Y, area.Y + area.Height - size.Height));
        WindowRuntimeState state = new(
            childId,
            owner.AppId,
            owner.OwnerInstanceId,
            title,
            owner.IconAssetPath,
            new WindowBounds(x, y, size.Width, size.Height),
            restoreBounds: null,
            zOrder: 0,
            WindowVisualState.Normal,
            WindowLaunchCoordinator.ToConstraints(options),
            options.Modal ? WindowModality.OwnerModal : WindowModality.Modeless,
            owner.Id,
            isFocused: false,
            content: content,
            // Close button, Alt+F4, the taskbar: the user cancels, as with a WinForms form.
            onRequestClose: () => child.CloseAsync(KazoDialogResult.Cancel).AsTask())
        {
            Frame = WindowLaunchCoordinator.ToFrame(options),
            DisplayId = owner.DisplayId,
        };
        _runtime.Apply(new CreateWindowCommand(state));
        _runtime.Apply(new FocusWindowCommand(childId));
        return child;
    }

    private static void Cascade<TValue>(RenderTreeBuilder builder, TValue value, RenderFragment childContent)
    {
        builder.OpenComponent<CascadingValue<TValue>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<TValue>.Value), value);
        builder.AddComponentParameter(2, nameof(CascadingValue<TValue>.IsFixed), true);
        builder.AddComponentParameter(3, nameof(CascadingValue<TValue>.ChildContent), childContent);
        builder.CloseComponent();
    }

    /// <summary>
    /// The platform's side of one child window (ADR 0234 §3): closes it with a result, and completes
    /// <see cref="Closed"/> however it goes away (its own request, its owner closing, its app stopping).
    /// </summary>
    private sealed class ChildWindow : IKazoWindow
    {
        private readonly WindowRuntime _runtime;
        private readonly WindowId _id;
        private readonly WindowId _ownerId;
        private readonly ContextMenuScope? _menus;
        private readonly TaskCompletionSource<KazoDialogResult> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private KazoDialogResult? _pendingResult;

        public ChildWindow(WindowRuntime runtime, WindowId id, WindowId ownerId, ContextMenuScope? menus)
        {
            _runtime = runtime;
            _id = id;
            _ownerId = ownerId;
            _menus = menus;
            _runtime.EventsApplied += OnEventsApplied;
        }

        public string Id => _id.ToString();

        public bool IsOpen => !_closed.Task.IsCompleted && _runtime.Windows.Any(window => window.Id == _id);

        public Task<KazoDialogResult> Closed => _closed.Task;

        public ValueTask<bool> CloseAsync(KazoDialogResult? result = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOpen)
            {
                return ValueTask.FromResult(false);
            }

            _pendingResult = result ?? KazoDialogResult.Cancel;
            _runtime.Apply(new ForceWindowCloseCommand(_id));
            return ValueTask.FromResult(true);
        }

        private void OnEventsApplied(IReadOnlyList<WindowEvent> events)
        {
            foreach (WindowEvent windowEvent in events)
            {
                if (windowEvent is not WindowClosedEvent closed)
                {
                    continue;
                }

                if (closed.WindowId == _id)
                {
                    Complete(_pendingResult ?? KazoDialogResult.None);
                    return;
                }

                if (closed.WindowId == _ownerId)
                {
                    // A child closes with its owner. The close is applied after the runtime has
                    // told every observer about the owner, so they see the facts in order.
                    _pendingResult ??= KazoDialogResult.None;
                    _ = CloseAfterOwnerAsync();
                    return;
                }
            }
        }

        private async Task CloseAfterOwnerAsync()
        {
            await Task.Yield();
            if (_runtime.Windows.Any(window => window.Id == _id))
            {
                _runtime.Apply(new ForceWindowCloseCommand(_id));
            }
            else
            {
                Complete(_pendingResult ?? KazoDialogResult.None);
            }
        }

        private void Complete(KazoDialogResult result)
        {
            _runtime.EventsApplied -= OnEventsApplied;
            _menus?.Dispose();
            _closed.TrySetResult(result);
        }
    }

    /// <summary>
    /// One window's gateway: reads and writes that window's title, frame and geometry in the runtime
    /// (ADR 0117 §2, ADR 0234 §4). Every request reaches this window only.
    /// </summary>
    private sealed class Gateway(
        AppWindowGatewayFactory factory,
        WindowId windowId,
        Func<string> defaultTitle,
        WindowOptions options,
        IAppExecutionContext? context) : IAppWindowGateway, IAppChildWindowGateway
    {
        private readonly WindowRuntime runtime = factory._runtime;

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

        public ValueTask<IKazoWindow?> OpenWindowAsync(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
            KazoWindowRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(componentType);
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            if (!typeof(KazoWindowBase).IsAssignableFrom(componentType))
            {
                throw new ArgumentException($"A child window's component must derive from {nameof(KazoWindowBase)}.", nameof(componentType));
            }

            // Child windows render in the app's context: a gateway created without one (a test, an
            // older host) cannot open them.
            if (context is null || Find() is not { } owner)
            {
                return ValueTask.FromResult<IKazoWindow?>(null);
            }

            return ValueTask.FromResult<IKazoWindow?>(factory.OpenChild(owner, context, componentType, request));
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
