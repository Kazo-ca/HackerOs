using System.Collections.Concurrent;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Network.Local;

/// <summary>
/// On-demand activation of web apps (ADR 0074, section 3), like systemd's socket activation: an
/// installed web app is not started at boot; the first request to one of its URLs starts it, waits
/// until it listens, then the request is handed to it. Stopped (task manager, <c>kill</c>), it is
/// started again by the next request.
/// </summary>
public sealed class WebAppActivator
{
    private readonly Func<AppLifecycleOrchestrator> _orchestrator;
    private readonly ListeningPortTable _ports;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _starting = new(StringComparer.Ordinal);

    /// <summary>Initializes the activator.</summary>
    /// <param name="orchestrator">
    /// Lazily resolves the orchestrator that launches the app: the orchestrator depends on the context
    /// factory, which hands this activator to every network gateway.
    /// </param>
    /// <param name="ports">The table the started app registers its socket in.</param>
    public WebAppActivator(Func<AppLifecycleOrchestrator> orchestrator, ListeningPortTable ports)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    /// <summary>Gets how long the OS waits for a started app to listen (the cold start of the web worker included).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Starts <paramref name="app"/> if nothing serves the target yet and waits for its socket. Only an
    /// app that declares the target's host and port is started.
    /// </summary>
    /// <param name="app">The web app whose URL names the host.</param>
    /// <param name="principal">The user the site runs as: the session of the caller.</param>
    /// <param name="host">Host name requested.</param>
    /// <param name="port">Port requested.</param>
    /// <param name="path">Path requested.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The socket that serves the target, or <see langword="null"/> (connection refused).</returns>
    internal async Task<OsHttpListenerSocket?> ActivateAsync(
        AppManifest app, AuthenticatedPrincipal principal, string host, int port, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(principal);
        bool declared = app.Web?.Urls.Any(url =>
            WebAppUrls.TryParse(url, allowWildcardHost: false, out WebBinding binding, out _)
            && binding.Port == port
            && string.Equals(binding.Host, host, StringComparison.OrdinalIgnoreCase)) == true;
        if (!declared)
        {
            return null;
        }

        // One start at a time per app: concurrent first requests (a page and its images) share it.
        SemaphoreSlim gate = _starting.GetOrAdd(app.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ports.Route(host, port, path, loopbackDestination: true) is { } existing)
            {
                return existing;
            }

            AppLaunchResult launched = await _orchestrator()
                .LaunchAsync(new AppLaunchRequest(app.Id, principal, [], Interactive: false), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (launched.Status is not (AppLaunchStatus.Launched or AppLaunchStatus.FocusedExisting))
            {
                return null;
            }

            return await _ports.WaitForRouteAsync(host, port, path, loopbackDestination: true, StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
