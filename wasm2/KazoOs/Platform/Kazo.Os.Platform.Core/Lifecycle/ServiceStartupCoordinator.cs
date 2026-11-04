using Kazo.Os.Platform.Core.Apps;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Sessions;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kazo.Os.Platform.Core.Lifecycle;

/// <summary>
/// The part of an init system that decides <em>when</em> services run (ADR 0097, section 4),
/// like systemd's <c>multi-user.target</c> and per-user <c>systemd --user</c> instance:
/// <list type="bullet">
/// <item>at the end of the boot (<see cref="StartBootServicesAsync"/>), the system services
/// (<see cref="Kazo.Os.App.Abstractions.AppServiceStartup.Boot"/>) start as <c>kazoos-svc</c>,
/// before the login screen;</item>
/// <item>after each login (<see cref="SessionActivatedEvent"/>), the user's automatic services
/// start as that user, once the user's <c>apps.config</c> is applied;</item>
/// <item>at the logout (<see cref="SessionLoggedOutEvent"/>), the session's services stop; the
/// system services keep running;</item>
/// <item>at the shutdown (<see cref="SessionShutDownEvent"/>), every service stops, then the
/// system scope is cancelled.</item>
/// </list>
/// A failure never blocks the boot or the login: it is logged, like a unit that fails to start
/// does not stop the machine from reaching the login prompt.
/// </summary>
public sealed class ServiceStartupCoordinator : IDisposable
{
    private readonly AppLifecycleOrchestrator _orchestrator;
    private readonly ISessionService _session;
    private readonly IProcessManager _processManager;
    private readonly AppInstallationService? _installation;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly IDisposable[] _subscriptions;
    private readonly InstallationId _installationId = InstallationId.FromGuid(Guid.NewGuid());
    private readonly DeviceId _deviceId = DeviceId.FromGuid(Guid.NewGuid());
    private int _bootStarted;

    /// <summary>Initializes the coordinator and subscribes to the session events.</summary>
    /// <param name="orchestrator">Starts and stops the services.</param>
    /// <param name="eventBus">Publishes the login, logout and shutdown.</param>
    /// <param name="session">Gives the logged-in principal.</param>
    /// <param name="processManager">Owns the system scope, cancelled at shutdown.</param>
    /// <param name="installation">Applies the user's <c>apps.config</c> before the login services start.</param>
    /// <param name="clock">Time stamps the service account's principal; defaults to the wall clock.</param>
    /// <param name="logger">Writes start failures to <c>/var/log/syslog</c>.</param>
    public ServiceStartupCoordinator(
        AppLifecycleOrchestrator orchestrator,
        IEventBus eventBus,
        ISessionService session,
        IProcessManager processManager,
        AppInstallationService? installation = null,
        Func<DateTimeOffset>? clock = null,
        ILogger<ServiceStartupCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _installation = installation;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _subscriptions =
        [
            eventBus.Subscribe<SessionActivatedEvent>(activated => Track(OnLoginAsync(activated))),
            eventBus.Subscribe<SessionLoggedOutEvent>(loggedOut => Track(OnLogoutAsync(loggedOut))),
            eventBus.Subscribe<SessionShutDownEvent>(_ => Track(OnShutdownAsync()))
        ];
    }

    /// <summary>Gets the work started by the last session event, for tests that wait for it.</summary>
    internal Task LastEventWork { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Starts the system services, once per boot (later calls do nothing). Called by the boot once
    /// storage is mounted and the machine-wide <c>apps.config</c> is applied.
    /// </summary>
    /// <returns>The launch results, empty when already started.</returns>
    public async Task<IReadOnlyList<AppLaunchResult>> StartBootServicesAsync()
    {
        if (Interlocked.Exchange(ref _bootStarted, 1) == 1)
        {
            return [];
        }

        try
        {
            return await _orchestrator.StartBootServicesAsync(
                ServiceAccount.CreatePrincipal(_installationId, _deviceId, _clock())).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "boot: the system services could not be started");
            return [];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private void Track(Task work) => LastEventWork = work;

    private async Task OnLoginAsync(SessionActivatedEvent activated)
    {
        // The event is raised inside the login: let it return before the services start.
        await Task.Yield();
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        if (principal is null || principal.SessionId != activated.SessionId)
        {
            return;
        }

        try
        {
            // The user's uninstalls are applied first, so an app the user removed never starts.
            if (_installation is not null)
            {
                await _installation.ReloadAsync().ConfigureAwait(false);
            }

            await _orchestrator.StartAllServicesAsync(principal).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "login: the services of {User} could not be started", principal.LoginName.Value);
        }
    }

    private async Task OnLogoutAsync(SessionLoggedOutEvent loggedOut)
    {
        try
        {
            await _orchestrator.StopSessionServicesAsync(loggedOut.SessionId, ProcessExitReason.Logout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "logout: the session's services could not all be stopped");
        }
    }

    private async Task OnShutdownAsync()
    {
        try
        {
            await _orchestrator.StopAllAsync(ProcessExitReason.Shutdown).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "shutdown: the services could not all be stopped");
        }
        finally
        {
            (_processManager as InMemoryProcessManager)?.CancelSystemScope();
        }
    }
}
