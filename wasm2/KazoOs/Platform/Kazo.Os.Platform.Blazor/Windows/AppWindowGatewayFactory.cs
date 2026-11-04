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
        return new Gateway(_runtime, windowId, () => _defaultTitle(manifest));
    }

    /// <summary>One window's gateway: reads and writes that window's title in the runtime.</summary>
    private sealed class Gateway(WindowRuntime runtime, WindowId windowId, Func<string> defaultTitle) : IAppWindowGateway
    {
        // Last title shown, kept for reads once the window has left the runtime (closing).
        private string? _lastTitle;

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

        private WindowRuntimeState? Find() => runtime.Windows.FirstOrDefault(window => window.Id == windowId);
    }
}
