using System.Diagnostics.CodeAnalysis;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Discovery;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Intents;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Processes;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HackerOs.Simulation.Abstractions.Time;

using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Shell;

namespace HackerOs.Platform.Core.Lifecycle;

/// <summary>
/// Starts, tracks, and stops app instances using catalog activation/deactivation order, per
/// `P1-APP-004` through `P1-APP-006` and `P1-APP-011`. This is the mechanism layer: it performs
/// every launch/stop/enable/disable it is asked to without itself deciding whether a caller is
/// allowed to ask. <see cref="AppIntentDispatcher"/> is the policy layer that capability-gates
/// requests before calling here.
/// </summary>
public sealed class AppLifecycleOrchestrator
{
    private readonly AppCatalog _catalog;
    private readonly IReadOnlyDictionary<string, AppDescriptor> _descriptors;
    private readonly AppEnablementRegistry _enablement;
    private readonly IProcessManager _processManager;
    private readonly ICapabilityGrantRepository _grants;
    private readonly AppExecutionContextFactory _contextFactory;
    private readonly IEventBus? _eventBus;
    private readonly IAppDescriptorLoader? _descriptorLoader;
    private readonly IServiceProvider? _services;
    private readonly IFileSystemService? _fileSystem;
    private readonly IElevationTicketStore? _elevationTickets;
    private readonly IDangerousCapabilityGate? _dangerousCapabilityGate;
    private readonly LoginEnvironmentProvider? _loginEnvironment;
    private readonly IRemoteProcessHost? _remoteHost;
    private readonly ISimulationClock? _clock;
    private readonly ILogger _logger;
    private readonly ServiceStateTable _serviceStates;
    private readonly object _sync = new();
    private readonly Dictionary<ProcessId, RunningInstance> _running = [];
    // Service processes of this boot, by PID, so the end of one updates its unit (ADR 0097).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ProcessId, string> _servicePids = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _bootRefusalLogged = new(StringComparer.Ordinal);

    /// <summary>Initializes the orchestrator over a fully resolved discovery result.</summary>
    /// <param name="catalog">Validated, dependency-ordered app catalog.</param>
    /// <param name="descriptors">Successfully resolved entry-point descriptors for the catalog.</param>
    /// <param name="enablement">Runtime enable/disable state for catalog apps.</param>
    /// <param name="processManager">Process lifecycle manager backing every launch.</param>
    /// <param name="grants">Capability grant repository used to compute each launch's granted set.</param>
    /// <param name="contextFactory">Trusted execution-context factory.</param>
    /// <param name="eventBus">Optional typed event bus for cross-subsystem lifecycle cleanup.</param>
    /// <param name="descriptorLoader">Optional lazy descriptor loader for assemblies not eagerly discovered.</param>
    /// <param name="services">
    /// Optional DI container used to construct terminal/service app instances (ADR 0034). When
    /// supplied, <see cref="RunTerminalAsync"/>/<see cref="StartService"/> use
    /// <see cref="ActivatorUtilities.CreateInstance(IServiceProvider, Type, object[])"/> so a
    /// command's constructor can request additional injected services beyond the manifest (e.g.
    /// <c>PingCommand</c>'s <c>IServerConnectionService</c>/<c>IProxyClient</c>). When omitted, only
    /// manifest-only constructors work — the previous, narrower behavior tests still rely on.
    /// </param>
    /// <param name="fileSystem">
    /// Optional trusted filesystem access used to resolve each <see cref="AppKind.Service"/> app's
    /// live <see cref="ServiceStartMode"/> from its per-user config file (see
    /// <see cref="ServiceStartModeStore"/>) before <see cref="StartAllServicesAsync"/> auto-starts
    /// it. When omitted (e.g. in tests that don't exercise start-mode gating), every enabled
    /// service is treated as <see cref="ServiceStartMode.Automatic"/> — the previous, narrower
    /// behavior tests still rely on.
    /// </param>
    /// <param name="elevationTickets">
    /// Optional single-use elevation ticket store (`ADR 0042`). When a <see cref="AppLaunchRequest.ElevationToken"/>
    /// is supplied, <see cref="LaunchAsync"/> consumes it here before starting the app. When omitted
    /// (e.g. in tests that don't exercise elevation), a supplied token is always denied — the
    /// previous, narrower behavior tests still rely on.
    /// </param>
    /// <param name="dangerousCapabilityGate">
    /// Optional launch gate of ADR 0049 section 5. When supplied, every launch of an app declaring
    /// a dangerous capability goes through it before the process is created (approval overlay,
    /// <c>auth.log</c>, grants aligned with <c>app-permissions.config</c>), and policy <c>block</c>
    /// refuses the one-shot approval of ADR 0042 for those capabilities. When omitted, a non-system
    /// app simply never holds a dangerous capability (the login seeder withholds them).
    /// </param>
    /// <param name="loginEnvironment">
    /// Optional source of the login environment (<c>/etc/environment</c>, ADR 0050 section 5) given
    /// to processes with no parent. When omitted, they get the fixed login variables only.
    /// </param>
    /// <param name="remoteHost">
    /// Optional out-of-thread host (ADR 0070): commands and services whose manifest asks for a worker
    /// run there. Registered by the WebAssembly hosts; when omitted (tests, a host without workers),
    /// every process runs in-process and <c>execution</c> is ignored.
    /// </param>
    /// <param name="clock">
    /// Optional simulation clock timestamping service state changes (ADR 0097); real time when omitted.
    /// </param>
    /// <param name="logger">
    /// Optional logger; a service entering <c>failed</c> is written there, hence to
    /// <c>/var/log/syslog</c> (ADR 0097, section 3).
    /// </param>
    public AppLifecycleOrchestrator(
        AppCatalog catalog,
        IReadOnlyDictionary<string, AppDescriptor> descriptors,
        AppEnablementRegistry enablement,
        IProcessManager processManager,
        ICapabilityGrantRepository grants,
        AppExecutionContextFactory contextFactory,
        IEventBus? eventBus = null,
        IAppDescriptorLoader? descriptorLoader = null,
        IServiceProvider? services = null,
        IFileSystemService? fileSystem = null,
        IElevationTicketStore? elevationTickets = null,
        IDangerousCapabilityGate? dangerousCapabilityGate = null,
        LoginEnvironmentProvider? loginEnvironment = null,
        IRemoteProcessHost? remoteHost = null,
        ISimulationClock? clock = null,
        ILogger<AppLifecycleOrchestrator>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _grants = grants ?? throw new ArgumentNullException(nameof(grants));
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _eventBus = eventBus;
        _descriptorLoader = descriptorLoader;
        _services = services;
        _fileSystem = fileSystem;
        _elevationTickets = elevationTickets;
        _dangerousCapabilityGate = dangerousCapabilityGate;
        _loginEnvironment = loginEnvironment;
        _remoteHost = remoteHost;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _clock = clock;
        _serviceStates = new ServiceStateTable(clock is null ? static () => DateTimeOffset.UtcNow : () => clock.UtcNow);
        _serviceStates.Failed += OnServiceFailed;
        _eventBus?.Subscribe<ProcessStateChangedEvent>(OnProcessStateChanged);
    }

    /// <summary>
    /// Error code of a launch refused by the <c>nproc</c> limit (ADR 0125): <c>fork</c> failed with
    /// <c>EAGAIN</c>, "Resource temporarily unavailable".
    /// </summary>
    public const string ProcessLimitErrorCode = "lifecycle.process.eagain";

    /// <summary>
    /// Gets the session limits of root processes (ADR 0125, section 3), read from
    /// <c>/etc/security/limits.conf</c> like <c>pam_limits</c>; <see langword="null"/> gives them the
    /// kernel's defaults.
    /// </summary>
    public LoginResourceLimitsProvider? LoginResourceLimits { get; init; }

    /// <summary>
    /// Gets the guard that backs up a user's data before the first launch of another version of an
    /// installed app (ADR 0214 §4.2); <see langword="null"/> launches without it.
    /// </summary>
    public AppDataGuard? DataGuard { get; init; }

    /// <summary>
    /// Gets or sets how long a service may stay <c>activating</c> (its <c>OnStartAsync</c>) before
    /// the OS kills it and marks it <c>failed (Result: timeout)</c> (ADR 0097, section 1). 30 s, like
    /// systemd's <c>TimeoutStartSec</c> default of 90 s scaled to the simulation.
    /// </summary>
    public TimeSpan ServiceStartTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the service manager's state of one <see cref="AppKind.Service"/> app (ADR 0097): active
    /// state, last result, health and status text. <see cref="ServiceStatusSnapshot.NeverStarted"/> for
    /// a service that has not run during this boot.
    /// </summary>
    /// <param name="appId">Service app ID.</param>
    public ServiceStatusSnapshot GetServiceStatus(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return _serviceStates.Get(appId);
    }

    /// <summary>Clears a service's <c>failed</c> state (<c>systemctl reset-failed</c>).</summary>
    /// <param name="appId">Service app ID.</param>
    /// <returns><see langword="true"/> when it was failed.</returns>
    public bool ResetFailedService(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return _serviceStates.ResetFailed(appId);
    }

    /// <summary>Gets the systemd unit name of a service app: <c>org.hackeros.hackwebd</c> is <c>hackwebd.service</c>.</summary>
    /// <param name="appId">Service app ID.</param>
    public static string UnitNameOf(string appId) => appId[(appId.LastIndexOf('.') + 1)..] + ".service";

    /// <summary>ADR 0097 section 3: a failure goes to <c>/var/log/syslog</c> with systemd's wording.</summary>
    private void OnServiceFailed(string appId, ServiceStatusSnapshot status)
    {
        string result = status.Result switch
        {
            ServiceRunResult.ExitCode => "exit-code",
            ServiceRunResult.Exception => "exception",
            ServiceRunResult.Killed => "killed",
            ServiceRunResult.Timeout => "timeout",
            _ => "success"
        };
        _logger.LogWarning("{Unit}: Failed with result '{Result}'.", UnitNameOf(appId), result);
    }

    /// <summary>
    /// Constructs a terminal/service app instance, resolving any constructor parameters beyond the
    /// manifest from DI when a service provider was supplied (ADR 0034) — falls back to the
    /// manifest-only shape when it wasn't, matching the pre-ADR-0034 behavior for tests that don't
    /// need extra dependencies.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming", "IL2072",
        Justification = "descriptor.EntryPointType was verified by AppEntryPointDiscovery against an " +
            "explicit host assembly allowlist, never scanned; a trimmed host keeps that assembly whole " +
            "(ADR 0113: generated root descriptor), constructors included.")]
    private object CreateAppInstance(AppDescriptor descriptor) =>
        _services is not null
            ? ActivatorUtilities.CreateInstance(_services, descriptor.EntryPointType, descriptor.Manifest)
            : Activator.CreateInstance(descriptor.EntryPointType, descriptor.Manifest)!;

    private void OnProcessStateChanged(ProcessStateChangedEvent e)
    {
        if (e.NewState == ProcessState.Stopping && _servicePids.TryGetValue(e.Pid, out string? stoppingService))
        {
            // SIGTERM from kill, End task or the OS itself: the unit is deactivating (ADR 0097).
            _serviceStates.MarkDeactivating(stoppingService, e.Pid);
        }

        if (e.NewState is ProcessState.Stopped or ProcessState.Faulted)
        {
            lock (_sync)
            {
                _running.Remove(e.Pid);
            }

            if (_servicePids.TryRemove(e.Pid, out string? endedService)
                && _processManager.GetHistory().LastOrDefault(record => record.Pid == e.Pid) is { } ended)
            {
                // Published inside the process manager's lock, which is re-entrant for this thread.
                _serviceStates.MarkEnded(endedService, ended);
            }
        }
    }

    /// <summary>Gets the enablement registry so callers can check status without a separate wire-up.</summary>
    public IAppEnablementRegistry Enablement => _enablement;

    /// <summary>
    /// Attempts to resolve a catalog app's discovered descriptor, exposing its resolved entry-point
    /// assembly for same-assembly authorization checks (e.g. a companion Window app controlling its
    /// own bundled Service app's start/stop/start-mode via the app-facing service-control gateway).
    /// </summary>
    public bool TryGetDescriptor(string appId, [NotNullWhen(true)] out AppDescriptor? descriptor) =>
        _descriptors.TryGetValue(appId, out descriptor);

    /// <summary>
    /// Gets a catalog app's descriptor, loading its assembly first when the host lazy-loads it
    /// (ADR 0063: an app's embeddable component lives in that assembly).
    /// </summary>
    /// <param name="appId">Catalog app id.</param>
    /// <param name="cancellationToken">Cancels the wait for the app's asset (the shared download continues).</param>
    /// <returns>The descriptor, or <see langword="null"/> with the reason when the app is unknown or its asset did not load.</returns>
    public async Task<(AppDescriptor? Descriptor, string? Error)> EnsureDescriptorAsync(string appId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (_descriptors.TryGetValue(appId, out AppDescriptor? descriptor))
        {
            return (descriptor, null);
        }

        if (_descriptorLoader is null)
        {
            return (null, $"The app '{appId}' is not available in this host.");
        }

        AppDescriptorLoadResult loaded = await _descriptorLoader
            .EnsureAvailableAsync(appId, cancellationToken)
            .ConfigureAwait(false);
        return loaded.Status == AppDescriptorLoadStatus.Available && _descriptors.TryGetValue(appId, out descriptor)
            ? (descriptor, null)
            : (null, loaded.Detail ?? $"The app '{appId}' is not available in this host.");
    }

    /// <summary>
    /// Hands a second activation to the running single instance (ADR 0096, section 5), like a
    /// <c>GApplication</c> that receives the command line of a second launch: the request's intent,
    /// or its arguments as a <see cref="AppIntentIds.LaunchApp"/> activation. A plain launch with no
    /// arguments only focuses the instance.
    /// </summary>
    private void DeliverToRunningInstance(ProcessRecord existing, AppLaunchRequest request)
    {
        AppIntentDelivery? delivery = request.Intent
            ?? (request.Arguments.Count > 0
                ? new AppIntentDelivery(AppIntentIds.LaunchApp) { Arguments = [.. request.Arguments] }
                : null);
        if (delivery is not null
            && TryGetRunningInstance(existing.Pid, out _, out IAppExecutionContext? running)
            && running is AppExecutionContext concrete)
        {
            concrete.DeliverIntent(delivery);
        }
    }

    /// <summary>Attempts to resolve the active descriptor and context for a running process.</summary>
    public bool TryGetRunningInstance(ProcessId pid, [NotNullWhen(true)] out AppDescriptor? descriptor, [NotNullWhen(true)] out IAppExecutionContext? context)
    {
        lock (_sync)
        {
            if (_running.TryGetValue(pid, out RunningInstance? instance))
            {
                descriptor = instance.Descriptor;
                context = instance.Context;
                return true;
            }
        }

        descriptor = null;
        context = null;
        return false;
    }

    /// <summary>
    /// Attempts to resolve the active descriptor and context for a running instance, for callers
    /// (e.g. the window runtime) that only carry the instance ID rather than the process ID.
    /// </summary>
    public bool TryGetRunningInstance(AppInstanceId instanceId, [NotNullWhen(true)] out AppDescriptor? descriptor, [NotNullWhen(true)] out IAppExecutionContext? context)
    {
        lock (_sync)
        {
            foreach (RunningInstance instance in _running.Values)
            {
                if (instance.Context.InstanceId == instanceId.Value)
                {
                    descriptor = instance.Descriptor;
                    context = instance.Context;
                    return true;
                }
            }
        }

        descriptor = null;
        context = null;
        return false;
    }

    /// <summary>
    /// Launches one catalog app. For a singleton <see cref="AppKind.Window"/> app that already has
    /// an active process, focuses the existing instance instead of starting a new one
    /// (`P1-APP-006`). <see cref="AppKind.Terminal"/> apps run to completion synchronously, since
    /// they represent one command execution rather than a persistent instance.
    /// </summary>
    /// <param name="request">Target app, acting principal, and launch arguments.</param>
    /// <param name="fullScreen">Optional alternate-screen session available only to Terminal apps.</param>
    /// <param name="cancellationToken">Cancels this launch or synchronous command execution.</param>
    public async Task<AppLaunchResult> LaunchAsync(
        AppLaunchRequest request,
        IFullScreenTerminalSession? fullScreen = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_descriptors.TryGetValue(request.AppId, out AppDescriptor? descriptor)
            && _descriptorLoader is not null)
        {
            AppDescriptorLoadResult loaded = await _descriptorLoader
                .EnsureAvailableAsync(request.AppId, cancellationToken)
                .ConfigureAwait(false);
            if (loaded.Status != AppDescriptorLoadStatus.Available
                || !_descriptors.TryGetValue(request.AppId, out descriptor))
            {
                return new AppLaunchResult(
                    AppLaunchStatus.NotFound,
                    ErrorCode: loaded.Status == AppDescriptorLoadStatus.Unavailable
                        ? "lifecycle.app.asset-unavailable"
                        : "lifecycle.app.not-found",
                    ErrorMessage: loaded.Detail);
            }
        }

        if (descriptor is null)
        {
            return new AppLaunchResult(AppLaunchStatus.NotFound, ErrorCode: "lifecycle.app.not-found");
        }

        if (!_enablement.IsEnabled(request.AppId))
        {
            return new AppLaunchResult(AppLaunchStatus.Disabled, ErrorCode: "lifecycle.app.disabled");
        }

        // A web site is one process, like a service (ADR 0073): a second activation finds it running.
        bool isImplicitlySingleton =
            (descriptor.Manifest.Kind == AppKind.Window && descriptor.Manifest.SingleInstancePerUser)
            || descriptor.Manifest.Kind is AppKind.Service or AppKind.Web;
        if (isImplicitlySingleton && _processManager.TryGetSingleton(request.AppId, out ProcessRecord existing))
        {
            DeliverToRunningInstance(existing, request);
            return new AppLaunchResult(AppLaunchStatus.FocusedExisting, existing);
        }

        bool isRootElevated = false;
        ElevationTicket? capabilityTicket = null;
        if (request.ElevationToken is Guid elevationToken && request.ElevationScope is ElevationScope elevationScope)
        {
            if (_elevationTickets is null
                || !_elevationTickets.TryConsume(elevationToken, elevationScope, out ElevationTicket? ticket)
                || ticket is null)
            {
                return new AppLaunchResult(AppLaunchStatus.ElevationDenied, ErrorCode: "lifecycle.elevation.denied");
            }

            if (elevationScope == ElevationScope.SudoRoot)
            {
                isRootElevated = true;
            }
            else if (ticket.Capability is not null)
            {
                capabilityTicket = ticket;
            }
        }

        // ADR 0049 section 5: decide pending dangerous capabilities before the process exists, so
        // the instance's capabilities are frozen at launch with the administrator's answer.
        DangerousCapabilityGateResult? dangerous = null;
        if (_dangerousCapabilityGate is not null
            && DangerousCapabilityEvaluator.DeclaresDangerousCapabilities(descriptor.Manifest))
        {
            dangerous = await _dangerousCapabilityGate
                .EnsureDecidedAsync(descriptor.Manifest, request.Principal, request.ApprovalPrompt, request.Interactive, cancellationToken)
                .ConfigureAwait(false);
        }

        OneShotElevationGrantSet? oneShotElevation = null;
        if (dangerous is { BlocksOneShotApproval: true })
        {
            // ADR 0049 section 3: under policy block the one-shot approval of ADR 0042 is refused
            // for every dangerous capability of a non-system app (sudo is not concerned).
            if (capabilityTicket?.Capability is { } ticketCapability && AppCapabilities.IsDangerous(ticketCapability))
            {
                return new AppLaunchResult(AppLaunchStatus.ElevationDenied, ErrorCode: "lifecycle.elevation.blocked-by-policy");
            }

            oneShotElevation = new OneShotElevationGrantSet();
            foreach (string capability in AppCapabilities.All.Where(AppCapabilities.IsDangerous))
            {
                oneShotElevation.Block(capability);
            }
        }

        if (capabilityTicket is not null)
        {
            oneShotElevation ??= new OneShotElevationGrantSet();
            oneShotElevation.Grant(capabilityTicket.Capability!, capabilityTicket.Path);
        }

        HashSet<string> grantedCapabilities = ComputeGrantedCapabilities(descriptor.Manifest, request.Principal);
        (VirtualPath? workingDirectory, IReadOnlyDictionary<string, string>? environment) =
            await ResolveStartEnvironmentAsync(request, cancellationToken).ConfigureAwait(false);
        environment = await GuardInstalledAppDataAsync(descriptor.Manifest, request, environment, cancellationToken).ConfigureAwait(false);
        // ADR 0125 section 3: a session's root process gets the limits of limits.conf; a boot
        // service (hackeros-svc, section 5) and every child get the kernel's or their parent's.
        ProcessResourceLimits? sessionLimits = request.ParentPid is null && !request.SystemScope && LoginResourceLimits is { } pamLimits
            ? await pamLimits.GetAsync(request.Principal, cancellationToken).ConfigureAwait(false)
            : null;
        ProcessRecord process;
        try
        {
            process = _processManager.Start(new ProcessStartRequest(
            request.ParentPid,
            request.AppId,
            AppInstanceId.FromGuid(Guid.NewGuid()),
            descriptor.Manifest.Kind,
            request.Principal.UserId,
            request.Principal.SessionId,
            // The manifest's declared weights feed the resource simulator, hence /proc/<pid>/status,
            // /proc/stat and /proc/meminfo (ADR 0012, ADR 0057 section 1). The manifest validator
            // already enforces the same [0, 1] and burst >= baseline rules as ResourceProfile.
            ToResourceProfile(descriptor.Manifest.Resources),
            // argv[0] is the name the command was invoked as (an alias such as `unalias` included),
            // otherwise the manifest's command name (ADR 0050).
            [request.CommandName ?? descriptor.Manifest.Terminal?.Name ?? request.AppId, .. request.Arguments],
            workingDirectory,
            environment,
            SystemScope: request.SystemScope)
            {
                Nice = request.Nice,
                ResourceLimits = sessionLimits,
                // ADR 0125 section 5: an elevated root is not held by nproc (CAP_SYS_RESOURCE).
                ResourceLimitExempt = isRootElevated,
                // ADR 0126 section 4: the manifest's oom_score_adj; null inherits the parent's.
                OomScoreAdjust = OomScoreAdjustOf(descriptor.Manifest)
            });
        }
        catch (ProcessLimitExceededException exception)
        {
            return new AppLaunchResult(AppLaunchStatus.EntryPointFault, ErrorCode: ProcessLimitErrorCode, ErrorMessage: exception.Message);
        }

        IAppExecutionContext context = _contextFactory.Create(
            descriptor.Manifest, request.Principal, process, grantedCapabilities, process.AppInstanceId.Value,
            isRootElevated, oneShotElevation);
        request.ProcessStarted?.Invoke(process);
        if (context is AppExecutionContext withIntent)
        {
            withIntent.Intent = request.Intent;
        }

        if (descriptor.Manifest.Kind != AppKind.Terminal
            && context is AppExecutionContext concrete
            && (request.StandardInput is not null || request.StandardOutput is not null || request.StandardError is not null))
        {
            // ADR 0064: a window or service started by a shell writes into that shell's terminal
            // (HackConsole). The window outlives the command line, so a closed pipe or terminal
            // must never fault it: its writes are then dropped, like output to a hung-up tty.
            concrete.Console = new AttachedAppConsole(
                request.StandardInput ?? TextReader.Null,
                new ForgivingTextWriter(request.StandardOutput ?? TextWriter.Null),
                new ForgivingTextWriter(request.StandardError ?? TextWriter.Null));
        }

        try
        {
            return descriptor.Manifest.Kind switch
            {
                AppKind.Terminal => await RunTerminalAsync(
                    descriptor, process, context, request, fullScreen,
                    // ADR 0051 section 3: the shell reaches only a command granted shell.control.
                    grantedCapabilities.Contains(AppCapabilities.ShellControl) ? request.Shell : null,
                    cancellationToken),
                AppKind.Service or AppKind.Web => StartService(descriptor, process, context),
                _ => StartWindow(descriptor, process, context)
            };
        }
        catch (Exception ex)
        {
            ProcessRecord faulted = _processManager.Fault(process.Pid);
            return new AppLaunchResult(
                AppLaunchStatus.EntryPointFault, faulted, context,
                ErrorCode: "lifecycle.entry-point.fault", ErrorMessage: ex.Message);
        }
    }

    /// <summary>
    /// Starts an auxiliary process of a web app (ADR 0084, section 3): the renderer of its Blazor
    /// part, drawn by the OS browser. Like <c>chrome --type=renderer</c>, it is a child of the
    /// browser's process, carries the site's app id and the caller's identity, and holds the
    /// capabilities granted to the site minus <c>network.listen</c> (client code listens on nothing).
    /// It is not the site's instance: on-demand activation (ADR 0074 §3) still starts the site, and
    /// stopping it (task manager, <c>kill</c>) ends only this renderer.
    /// </summary>
    /// <param name="appId">The web app whose component is rendered.</param>
    /// <param name="principal">The user of the browser tab.</param>
    /// <param name="parentPid">The browser window's process.</param>
    /// <param name="arguments"><c>argv</c> after the app name (<c>--type=renderer</c>, the URL).</param>
    /// <param name="cancellationToken">Cancels the load of the app's assembly.</param>
    /// <returns>The launch: the process and its context, or why it did not start.</returns>
    public async Task<AppLaunchResult> StartAuxiliaryAsync(
        string appId,
        AuthenticatedPrincipal principal,
        ProcessId parentPid,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(arguments);
        (AppDescriptor? descriptor, string? error) = await EnsureDescriptorAsync(appId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return new AppLaunchResult(AppLaunchStatus.NotFound, ErrorCode: "lifecycle.app.not-found", ErrorMessage: error);
        }

        if (descriptor.Manifest.Kind != AppKind.Web)
        {
            return new AppLaunchResult(AppLaunchStatus.NotFound, ErrorCode: "lifecycle.auxiliary.not-web", ErrorMessage: $"'{appId}' is not a web app.");
        }

        if (!_enablement.IsEnabled(appId))
        {
            return new AppLaunchResult(AppLaunchStatus.Disabled, ErrorCode: "lifecycle.app.disabled");
        }

        HashSet<string> granted = ComputeGrantedCapabilities(descriptor.Manifest, principal);
        granted.Remove(AppCapabilities.NetworkListen);
        granted.Remove(AppCapabilities.NetworkListenPrivileged);
        ProcessRecord process = _processManager.Start(new ProcessStartRequest(
            parentPid,
            appId,
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Web,
            principal.UserId,
            principal.SessionId,
            ToResourceProfile(descriptor.Manifest.Resources),
            [appId, .. arguments],
            Auxiliary: true));
        IAppExecutionContext context = _contextFactory.Create(
            descriptor.Manifest, principal, process, granted, process.AppInstanceId.Value);

        // Tracked like a window: no run task, stopped by StopAsync (a SIGTERM closes it at once).
        lock (_sync)
        {
            _running[process.Pid] = new RunningInstance(descriptor, context, ServiceInstance: null, RunTask: null);
        }

        if (context is AppExecutionContext trusted)
        {
            ProcessId pid = process.Pid;
            trusted.Control.StopRequested += request => _ = StopAsync(pid, request.Reason);
        }

        ProcessRecord running = _processManager.MarkRunning(process.Pid);
        return new AppLaunchResult(AppLaunchStatus.Launched, running, context);
    }

    /// <summary>
    /// Starts every enabled, automatic session <see cref="AppKind.Service"/> app in catalog
    /// activation order, per `P1-APP-005` and ADR 0097 section 4: run once per new session, right
    /// after the login, as the logged-in user. System services (<see cref="AppServiceStartup.Boot"/>)
    /// are left to <see cref="StartBootServicesAsync"/>.
    /// </summary>
    /// <param name="principal">Authenticated principal the services run as.</param>
    public async Task<IReadOnlyList<AppLaunchResult>> StartAllServicesAsync(AuthenticatedPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        List<AppLaunchResult> results = [];
        foreach (AppDescriptor descriptor in AutomaticServiceCandidates(AppServiceStartup.Login))
        {
            string appId = descriptor.Manifest.Id;
            ServiceStartMode effectiveMode = _fileSystem is null
                ? ServiceStartMode.Automatic
                : await ServiceStartModeStore.ReadAsync(
                    _fileSystem,
                    principal.LoginName.Value,
                    appId,
                    descriptor.Manifest.AutoStart ? ServiceStartMode.Automatic : ServiceStartMode.Manual,
                    CancellationToken.None);

            if (effectiveMode != ServiceStartMode.Automatic)
            {
                continue;
            }

            // Nobody is watching an auto-start: pending dangerous capabilities are deferred (ADR 0049 section 5.4).
            results.Add(await LaunchAsync(new AppLaunchRequest(appId, principal, [], Interactive: false)));
        }

        return results;
    }

    /// <summary>
    /// Starts every enabled, automatic system service (<see cref="AppServiceStartup.Boot"/>, system
    /// apps only) in catalog activation order, once storage is mounted and before the login screen
    /// (ADR 0097, section 4). They run as <paramref name="serviceAccount"/> (<c>hackeros-svc</c>), in
    /// the system scope: a logout does not stop them, only a shutdown does. Their start mode is read
    /// from <c>/etc/&lt;appId&gt;/service.conf</c>.
    /// </summary>
    /// <param name="serviceAccount">The service account's principal (<c>ServiceAccount.CreatePrincipal</c>).</param>
    public async Task<IReadOnlyList<AppLaunchResult>> StartBootServicesAsync(AuthenticatedPrincipal serviceAccount)
    {
        ArgumentNullException.ThrowIfNull(serviceAccount);

        List<AppLaunchResult> results = [];
        foreach (AppDescriptor descriptor in AutomaticServiceCandidates(AppServiceStartup.Boot))
        {
            string appId = descriptor.Manifest.Id;
            ServiceStartMode effectiveMode = _fileSystem is null
                ? ServiceStartMode.Automatic
                : await ServiceStartModeStore.ReadSystemAsync(
                    _fileSystem,
                    appId,
                    descriptor.Manifest.AutoStart ? ServiceStartMode.Automatic : ServiceStartMode.Manual,
                    CancellationToken.None);
            if (effectiveMode != ServiceStartMode.Automatic)
            {
                continue;
            }

            // A system service holds its declared capabilities for the service account, as a system
            // app's do for every user at login (ADR 0049).
            CleanProfileCapabilityGrantSeeder.SeedDeclaredCapabilities(
                _grants, descriptor.Manifest, serviceAccount.UserId.ToString(), isSystemApp: true);
            AppLaunchResult launched = await LaunchAsync(new AppLaunchRequest(appId, serviceAccount, [], Interactive: false, SystemScope: true));
            if (launched.Status is AppLaunchStatus.Launched)
            {
                _logger.LogInformation("{Unit}: started at boot as {User}", UnitNameOf(appId), serviceAccount.LoginName.Value);
            }

            results.Add(launched);
        }

        return results;
    }

    /// <summary>
    /// Stops the services and web apps of one login session (ADR 0097, section 4): at the logout,
    /// every service the session started is stopped through the bounded stop path; the system
    /// services keep running.
    /// </summary>
    /// <param name="sessionId">The session that ended.</param>
    /// <param name="reason">Reason recorded against stopped processes (<see cref="ProcessExitReason.Logout"/>).</param>
    /// <returns>How many instances were stopped.</returns>
    public async Task<int> StopSessionServicesAsync(SessionId sessionId, ProcessExitReason reason = ProcessExitReason.Logout)
    {
        List<KeyValuePair<ProcessId, RunningInstance>> snapshot;
        lock (_sync)
        {
            snapshot = [.. _running.Where(entry =>
                entry.Value.RunTask is not null
                && entry.Value.Context.SessionId == sessionId)];
        }

        foreach ((ProcessId pid, RunningInstance instance) in snapshot)
        {
            await StopInstanceAsync(pid, instance, reason).ConfigureAwait(false);
        }

        // A service that ended on the cancelled session token before the OS could stop it is
        // ended here, so its unit does not stay active (ADR 0097).
        foreach (ProcessRecord orphan in _processManager.GetActiveProcesses()
            .Where(record => record.SessionId == sessionId && _servicePids.ContainsKey(record.Pid)))
        {
            try
            {
                _processManager.Kill(orphan.Pid, reason);
            }
            catch (KeyNotFoundException)
            {
                // Ended concurrently.
            }
        }

        return snapshot.Count;
    }

    /// <summary>
    /// Stops every app instance of one login session, windows included, through the bounded stop
    /// path (each app may keep a recovery draft first): what a sign-out does before the session
    /// ends, as <c>loginctl terminate-session</c>. The shell still shown closes their windows, so
    /// none is left for the next session.
    /// </summary>
    /// <param name="sessionId">The session that is ending.</param>
    /// <param name="reason">Reason recorded against stopped processes (<see cref="ProcessExitReason.Logout"/>).</param>
    /// <returns>How many instances were stopped.</returns>
    public async Task<int> StopSessionAppsAsync(SessionId sessionId, ProcessExitReason reason = ProcessExitReason.Logout)
    {
        List<KeyValuePair<ProcessId, RunningInstance>> snapshot;
        lock (_sync)
        {
            snapshot = [.. _running.Where(entry => entry.Value.Context.SessionId == sessionId)];
        }

        foreach ((ProcessId pid, RunningInstance instance) in snapshot)
        {
            await StopInstanceAsync(pid, instance, reason).ConfigureAwait(false);
        }

        return snapshot.Count;
    }

    /// <summary>
    /// Gets when an automatic service actually starts (ADR 0097, section 4): <see cref="AppServiceStartup.Boot"/>
    /// only for a system app; a non-system app asking for it starts at login, with a warning in
    /// <c>/var/log/syslog</c> (once per boot).
    /// </summary>
    /// <param name="manifest">A service app's manifest.</param>
    public AppServiceStartup GetEffectiveStartup(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Kind != AppKind.Service || manifest.ServiceStartup != AppServiceStartup.Boot)
        {
            return AppServiceStartup.Login;
        }

        if (_catalog.IsSystemApp(manifest.Id))
        {
            return AppServiceStartup.Boot;
        }

        if (_bootRefusalLogged.TryAdd(manifest.Id, 0))
        {
            _logger.LogWarning(
                "{Unit}: asks to start at boot but is not a system app; it starts at login instead",
                UnitNameOf(manifest.Id));
        }

        return AppServiceStartup.Login;
    }

    private IEnumerable<AppDescriptor> AutomaticServiceCandidates(AppServiceStartup startup)
    {
        foreach (string appId in _catalog.ActivationOrder)
        {
            if (_descriptors.TryGetValue(appId, out AppDescriptor? descriptor)
                && descriptor.Manifest.Kind == AppKind.Service
                && _enablement.IsEnabled(appId)
                && GetEffectiveStartup(descriptor.Manifest) == startup)
            {
                yield return descriptor;
            }
        }
    }

    /// <summary>
    /// Stops every currently running app instance in catalog deactivation order, per `P1-APP-005`.
    /// Each <see cref="AppKind.Service"/> instance receives a bounded graceful stop callback
    /// before its process is force-stopped; other kinds are force-stopped directly.
    /// </summary>
    /// <param name="reason">Reason recorded against every stopped process.</param>
    public async Task StopAllAsync(ProcessExitReason reason)
    {
        List<KeyValuePair<ProcessId, RunningInstance>> snapshot;
        lock (_sync)
        {
            snapshot = [.. _running];
        }

        Dictionary<string, List<KeyValuePair<ProcessId, RunningInstance>>> byAppId = new(StringComparer.Ordinal);
        foreach (KeyValuePair<ProcessId, RunningInstance> entry in snapshot)
        {
            if (!byAppId.TryGetValue(entry.Value.Descriptor.Manifest.Id, out List<KeyValuePair<ProcessId, RunningInstance>>? list))
            {
                byAppId[entry.Value.Descriptor.Manifest.Id] = list = [];
            }

            list.Add(entry);
        }

        foreach (string appId in _catalog.DeactivationOrder)
        {
            if (!byAppId.TryGetValue(appId, out List<KeyValuePair<ProcessId, RunningInstance>>? instances))
            {
                continue;
            }

            foreach ((ProcessId pid, RunningInstance instance) in instances)
            {
                await StopInstanceAsync(pid, instance, reason);
            }
        }
    }

    /// <summary>Stops one running app instance through the same bounded lifecycle path.</summary>
    /// <param name="pid">Process identity of the instance to stop.</param>
    /// <param name="reason">Terminal reason recorded for the process.</param>
    /// <returns><see langword="true"/> when an active instance was found and stopped.</returns>
    public async Task<bool> StopAsync(ProcessId pid, ProcessExitReason reason = ProcessExitReason.CloseRequested)
    {
        RunningInstance? instance;
        lock (_sync)
        {
            _running.TryGetValue(pid, out instance);
        }

        if (instance is null)
        {
            return false;
        }

        await StopInstanceAsync(pid, instance, reason);
        return true;
    }

    /// <summary>
    /// Stops one running app instance identified by its instance rather than process identity, for
    /// callers (e.g. the window runtime) that only carry the instance ID. An instance ID is unique
    /// to one process for the whole life of that process, so this is equivalent to
    /// <see cref="StopAsync(ProcessId, ProcessExitReason)"/> once the owning process is resolved.
    /// </summary>
    /// <param name="instanceId">Instance identity of the process to stop.</param>
    /// <param name="reason">Terminal reason recorded for the process.</param>
    /// <returns><see langword="true"/> when an active instance was found and stopped.</returns>
    public async Task<bool> StopAsync(AppInstanceId instanceId, ProcessExitReason reason = ProcessExitReason.CloseRequested)
    {
        ProcessId pid;
        RunningInstance? instance;
        lock (_sync)
        {
            KeyValuePair<ProcessId, RunningInstance> found = _running
                .FirstOrDefault(entry => entry.Value.Context.InstanceId == instanceId.Value);
            instance = found.Value;
            pid = found.Key;
        }

        if (instance is null)
        {
            return false;
        }

        await StopInstanceAsync(pid, instance, reason);
        return true;
    }

    /// <summary>
    /// Stops the running instance of one <see cref="AppKind.Service"/> app, identified by app ID
    /// rather than process ID — services are implicitly singleton per user (see
    /// <see cref="LaunchAsync"/>), so at most one running instance can exist.
    /// </summary>
    /// <param name="appId">Service app to stop.</param>
    /// <param name="reason">Terminal reason recorded for the process.</param>
    /// <returns><see langword="true"/> when a running instance was found and stopped.</returns>
    public async Task<bool> StopServiceAsync(string appId, ProcessExitReason reason = ProcessExitReason.CloseRequested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return _processManager.TryGetSingleton(appId, out ProcessRecord existing)
            && await StopAsync(existing.Pid, reason);
    }

    /// <summary>
    /// Disables one app and every enabled app that transitively depends on it, stopping their
    /// active processes in dependents-first order (`P1-APP-011`). Internal since ADR 0048: only
    /// <c>AppInstallationService</c> calls it, to enforce an uninstall written in <c>apps.config</c>.
    /// File-association preferences are deliberately left untouched, so a reinstall finds them again;
    /// <see cref="FileAssociationResolver"/> already ignores a disabled app.
    /// </summary>
    /// <param name="appId">App to disable.</param>
    /// <param name="reason">Reason recorded against stopped processes.</param>
    internal async Task<AppDisableResult> DisableAsync(string appId, ProcessExitReason reason = ProcessExitReason.Disabled)
    {
        if (!_catalog.Manifests.ContainsKey(appId))
        {
            return new AppDisableResult(false, [], [$"lifecycle.app.not-found:{appId}"]);
        }

        if (!_enablement.IsEnabled(appId))
        {
            return new AppDisableResult(true, [], []);
        }

        IReadOnlyList<string> closure = _enablement.ComputeDisableClosure(appId);
        List<AppProcessInfo> stopped = [];
        foreach (string toDisable in closure)
        {
            List<KeyValuePair<ProcessId, RunningInstance>> instances;
            lock (_sync)
            {
                instances = _running.Where(e => e.Value.Descriptor.Manifest.Id == toDisable).ToList();
            }

            foreach ((ProcessId pid, RunningInstance instance) in instances)
            {
                bool forced = await StopInstanceAsync(pid, instance, reason);
                stopped.Add(Describe(pid, instance) with { Forced = forced });
            }
        }

        _enablement.MarkDisabled(closure);
        foreach (string disabledAppId in closure)
        {
            _eventBus?.Publish(new AppDisabledEvent(disabledAppId));
        }

        return new AppDisableResult(true, closure, [], stopped);
    }

    /// <summary>
    /// Gets the processes this orchestrator runs for the given apps (windows, services, sites and
    /// their renderers), by PID: what an uninstall of those apps would stop (ADR 0097, section 5).
    /// </summary>
    /// <param name="appIds">The apps.</param>
    public IReadOnlyList<AppProcessInfo> GetRunningProcesses(IEnumerable<string> appIds)
    {
        ArgumentNullException.ThrowIfNull(appIds);
        HashSet<string> wanted = new(appIds, StringComparer.Ordinal);
        lock (_sync)
        {
            return [.. _running
                .Where(entry => wanted.Contains(entry.Value.Descriptor.Manifest.Id))
                .OrderBy(entry => entry.Key.Value)
                .Select(entry => Describe(entry.Key, entry.Value))];
        }
    }

    private static AppProcessInfo Describe(ProcessId pid, RunningInstance instance) => new(
        pid.Value,
        instance.Descriptor.Manifest.Id,
        instance.Descriptor.Manifest.Name,
        instance.Context.UserLoginName);

    /// <summary>
    /// Re-enables one app, failing with an explanatory error when a non-optional dependency is
    /// missing or still disabled (`P1-APP-011`). Internal since ADR 0048, like <see cref="DisableAsync"/>.
    /// </summary>
    /// <param name="appId">App to enable.</param>
    internal Task<AppEnableResult> EnableAsync(string appId)
    {
        if (!_catalog.Manifests.ContainsKey(appId))
        {
            return Task.FromResult(new AppEnableResult(false, [$"lifecycle.app.not-found:{appId}"]));
        }

        if (_enablement.IsEnabled(appId))
        {
            return Task.FromResult(new AppEnableResult(true, []));
        }

        IReadOnlyList<string> blocking = _enablement.ComputeBlockingDependencies(appId);
        if (blocking.Count > 0)
        {
            return Task.FromResult(new AppEnableResult(
                false,
                [.. blocking.Select(dependency => $"lifecycle.enable.dependency-disabled:{dependency}")]));
        }

        _enablement.MarkEnabled(appId);
        return Task.FromResult(new AppEnableResult(true, []));
    }

    private HashSet<string> ComputeGrantedCapabilities(AppManifest manifest, AuthenticatedPrincipal principal)
    {
        string userId = principal.UserId.ToString();
        HashSet<string> granted = new(StringComparer.Ordinal);
        foreach (string capability in manifest.Capabilities)
        {
            CapabilityPolicyEvaluation evaluation = _grants.Evaluate(
                manifest.Id, userId, capability, principal.Authority, AppAuthority.User);
            if (evaluation.Granted)
            {
                granted.Add(capability);
            }
        }

        return granted;
    }

    private async Task<AppLaunchResult> RunTerminalAsync(
        AppDescriptor descriptor,
        ProcessRecord process,
        IAppExecutionContext context,
        AppLaunchRequest request,
        IFullScreenTerminalSession? fullScreen,
        IShellSession? shell,
        CancellationToken cancellationToken)
    {
        _processManager.MarkRunning(process.Pid);

        // ADR 0050 section 6: the context keeps the process's state at command start, for
        // compatibility; the live values are read through HackEnvironment.
        IAppEnvironmentGateway processEnvironment = context.Environment;

        // ADR 0070: a command hosted in a worker is instantiated there, not here.
        TerminalAppBase? app = _remoteHost?.ShouldRunRemotely(descriptor.Manifest) == true
            ? null
            : (TerminalAppBase)CreateAppInstance(descriptor);
        // ADR 0050 section 4: streams supplied by the shell (terminal, pipe, redirected file) are
        // written as the command runs; without them the output is buffered for the caller.
        using StringWriter? bufferedOutput = request.StandardOutput is null ? new StringWriter() : null;
        using StringWriter? bufferedError = request.StandardError is null ? new StringWriter() : null;
        using StringReader emptyInput = new(string.Empty);
        using CancellationTokenSource executionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken);
        TerminalExecutionContext terminalContext = new(
            context,
            request.Arguments,
            request.StandardInput ?? emptyInput,
            request.StandardOutput ?? bufferedOutput!,
            request.StandardError ?? bufferedError!,
            processEnvironment.CurrentDirectory,
            processEnvironment.GetVariables(),
            fullScreen,
            shell);

        if (context is AppExecutionContext concreteContext)
        {
            // HackConsole in a command is its terminal context's streams.
            concreteContext.Console = new AttachedAppConsole(
                terminalContext.StandardInput, terminalContext.StandardOutput, terminalContext.StandardError);
        }

        List<FileSystemError> permissionDenials = [];
        void OnPermissionDenied(object? sender, AppPermissionErrorEventArgs e) => permissionDenials.Add(e.Error);
        context.PermissionErrors.PermissionDenied += OnPermissionDenied;

        int exitCode;
        Task<int>? remoteRun = null;
        try
        {
            // ADR 0043/0050: the command runs as this app, in its process's working directory (the
            // shell's, copied at creation), so the Hack* file API resolves relative paths exactly
            // like the shell does, and follows the command's own HackEnvironment changes.
            using IDisposable ambient = HackExecutionContext.Enter(context);
            // ADR 0069: the command runs on its process's scheduler, so a kill drops its leftovers.
            exitCode = app is null
                // ADR 0070: the command runs in a worker; the OS keeps its process, context and streams.
                ? await RunAsProcessAsync(context, () => remoteRun = RunRemoteTerminalAsync(descriptor, context, terminalContext, executionCancellation.Token))
                : await RunAsProcessAsync(context, () => app.ExecuteAsync(terminalContext, executionCancellation.Token).AsTask());
        }
        catch (HackProcessKilledException)
        {
            // SIGKILL (kill -9, or a SIGTERM whose grace period ran out): 128 + 9, like a shell reports it.
            exitCode = 137;
            await WaitForKilledRemoteOutputAsync(remoteRun).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested || context.StopRequest is not null)
        {
            // Also when HackProcess.CheckpointAsync saw the stop request just before the token was cancelled (ADR 0069).
            // ADR 0125 section 2: ended by the SIGXCPU of its CPU limit, 128 + 24.
            exitCode = StoppedExitCode(context);
        }
        catch (ShellBrokenPipeException)
        {
            // ADR 0050 section 4: the reader of its output exited and the command let the "Broken
            // pipe" error escape, as a Linux process killed by SIGPIPE (128 + 13).
            exitCode = 141;
        }
        finally
        {
            context.PermissionErrors.PermissionDenied -= OnPermissionDenied;
        }

        ProcessRecord stopped = CompleteUnlessEnded(process, exitCode);
        return new AppLaunchResult(
            AppLaunchStatus.Launched, stopped, context, exitCode,
            bufferedOutput?.ToString() ?? string.Empty, bufferedError?.ToString() ?? string.Empty,
            PermissionDenials: permissionDenials);
    }

    /// <summary>
    /// Runs a command in a worker (ADR 0070) and maps how it ended to what the same command would
    /// have given in-process: its exit code, <see cref="HackProcessKilledException"/> for a kill
    /// (137), 130 for an interruption, 141 for a broken pipe, and an exception for a fault.
    /// </summary>
    private async Task<int> RunRemoteTerminalAsync(
        AppDescriptor descriptor,
        IAppExecutionContext context,
        TerminalExecutionContext terminalContext,
        CancellationToken cancellationToken)
    {
        RemoteProcessExit exit = await _remoteHost!.RunTerminalAsync(
            new RemoteProcessLaunch(descriptor, context, ControlOf(context), ExecutionPlacement.CurrentGroup),
            terminalContext,
            cancellationToken).ConfigureAwait(false);
        return exit.Status switch
        {
            RemoteProcessExitStatus.Exited => exit.ExitCode,
            RemoteProcessExitStatus.Killed => throw new HackProcessKilledException(context.ProcessId.Value),
            RemoteProcessExitStatus.Cancelled => StoppedExitCode(context),
            RemoteProcessExitStatus.BrokenPipe => 141,
            _ => throw new RemoteProcessFaultException(exit.FaultMessage ?? "The command faulted in its worker.")
        };
    }

    /// <summary>How long a killed command in a worker may take to write the output the OS already received.</summary>
    internal static readonly TimeSpan KilledRemoteOutputTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// A killed command in a worker ends for its caller only once its session wrote the output the OS
    /// had received: what an in-process command wrote before <c>SIGKILL</c> is already in its pipe,
    /// and the same must hold in a worker, where the output arrives as messages. The session ends
    /// when the worker confirms the kill, or when the OS terminates the worker (ADR 0069, steps 3
    /// and 4), so the wait is short and bounded.
    /// </summary>
    private static async Task WaitForKilledRemoteOutputAsync(Task<int>? remoteRun)
    {
        if (remoteRun is null)
        {
            return;
        }

        try
        {
            await remoteRun.WaitAsync(KilledRemoteOutputTimeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Killed, as expected, or still running after the bound: it ended for the OS anyway.
        }
    }

    /// <summary>
    /// The exit code of a command that ended on its stop request: 152 (128 + <c>SIGXCPU</c>) when the
    /// request was its CPU limit (ADR 0125, section 2), otherwise 130, as before.
    /// </summary>
    /// <remarks>
    /// A process killed while it was being cancelled (<c>SIGKILL</c> of a hard CPU limit cancels its token
    /// too) reports 137: the kill wins, as the kernel reports the signal that ended it.
    /// </remarks>
    private static int StoppedExitCode(IAppExecutionContext context) =>
        ControlOf(context)?.IsKilled == true ? 137
        : context.StopRequest?.Reason == ProcessExitReason.CpuLimitExceeded ? InMemoryProcessManager.CpuLimitExitCode
        : 130;

    private static ProcessControlBlock? ControlOf(IAppExecutionContext context) =>
        context is AppExecutionContext trusted ? trusted.Control : null;

    /// <summary>
    /// Waits until a process has ended (ADR 0064): the shell waits for a window app it started in
    /// the foreground, as a Linux shell waits for a GUI program until its window closes.
    /// </summary>
    /// <param name="pid">The process to wait for.</param>
    /// <param name="cancellationToken">Stops waiting (the process keeps running).</param>
    /// <returns>The process's exit code, or <c>0</c> when it ended without one (window closed).</returns>
    public async Task<int> WaitForExitAsync(ProcessId pid, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = _eventBus?.Subscribe<ProcessStateChangedEvent>(e =>
        {
            if (e.Pid == pid && e.NewState is ProcessState.Stopped or ProcessState.Faulted)
            {
                ended.TrySetResult();
            }
        });
        try
        {
            while (_processManager.TryGetActive(pid, out _))
            {
                // The event wakes the wait at once; the poll covers a manager without an event bus.
                Task delay = Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                await Task.WhenAny(ended.Task, delay).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            subscription?.Dispose();
        }

        ProcessRecord? record = _processManager.GetHistory().LastOrDefault(r => r.Pid == pid);
        return record?.ExitCode ?? (record?.State == ProcessState.Faulted ? 1 : 0);
    }

    /// <summary>
    /// Starts an <c>hsh</c> process that runs a shell script (ADR 0064), as a child of
    /// <paramref name="parent"/>: like <c>fork</c> + <c>exec</c> of <c>/bin/sh script</c>, it keeps
    /// the parent shell's capabilities and root elevation, and gets its own directory and
    /// environment copied from the parent.
    /// </summary>
    /// <param name="parent">The context of the shell running the script.</param>
    /// <param name="principal">The user the script runs as.</param>
    /// <param name="arguments"><c>argv</c> of the process: the script path then its arguments.</param>
    /// <param name="workingDirectory">The directory the script starts in.</param>
    /// <param name="environment">Variables applied over the inherited ones (a subshell's own environment, prefix assignments).</param>
    /// <param name="processStarted">Called with the new process (background jobs record it).</param>
    /// <returns>The process and its trusted context; the caller completes the process.</returns>
    /// <exception cref="ProcessLimitExceededException">The user's <c>nproc</c> limit is reached (<c>EAGAIN</c>, ADR 0125).</exception>
    public (ProcessRecord Process, IAppExecutionContext Context) StartShellProcess(
        IAppExecutionContext parent,
        AuthenticatedPrincipal principal,
        IReadOnlyList<string> arguments,
        VirtualPath workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        Action<ProcessRecord>? processStarted = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(arguments);
        Dictionary<string, string>? overrides = environment is null ? null : new(environment, StringComparer.Ordinal);
        if (overrides is not null)
        {
            overrides["PWD"] = workingDirectory.Value;
        }

        ProcessRecord process = _processManager.Start(new ProcessStartRequest(
            parent.ProcessId,
            ShellHostService.ShellAppId,
            AppInstanceId.FromGuid(Guid.NewGuid()),
            AppKind.Terminal,
            principal.UserId,
            principal.SessionId,
            ResourceProfile.None,
            arguments,
            workingDirectory,
            overrides ?? new Dictionary<string, string>(StringComparer.Ordinal) { ["PWD"] = workingDirectory.Value })
        {
            // ADR 0125 section 5: an elevated root is not held by nproc.
            ResourceLimitExempt = parent.IsRootElevated
        });
        try
        {
            _processManager.MarkRunning(process.Pid);
            IAppExecutionContext context = _contextFactory.Create(
                ShellHostService.ShellManifest, principal, process, parent.GrantedCapabilities,
                process.AppInstanceId.Value, parent.IsRootElevated);
            processStarted?.Invoke(process);
            return (process, context);
        }
        catch
        {
            if (_processManager.TryGetActive(process.Pid, out _))
            {
                _processManager.Kill(process.Pid);
            }

            throw;
        }
    }

    /// <summary>Ends a script's <c>hsh</c> process with its exit status, unless it already ended (killed).</summary>
    public void CompleteShellProcess(ProcessId pid, int exitCode)
    {
        if (_processManager.TryGetActive(pid, out _))
        {
            try
            {
                _processManager.Complete(pid, exitCode);
            }
            catch (KeyNotFoundException)
            {
                // Ended concurrently.
            }
        }
    }

    /// <summary>Stops a running window or service instance and its process (Ctrl+C on a foreground window app).</summary>
    public async Task<bool> InterruptAsync(ProcessId pid)
    {
        if (await StopAsync(pid, ProcessExitReason.Killed).ConfigureAwait(false))
        {
            return true;
        }

        if (_processManager.TryGetActive(pid, out _))
        {
            _processManager.Kill(pid);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records a command's exit, unless its process already ended: killed with <c>kill</c>, or with
    /// the shell that ran it when its terminal closed (ADR 0056, section 7). The record of that
    /// earlier end is kept, as the exit of a process killed by a signal is the signal's.
    /// </summary>
    private ProcessRecord CompleteUnlessEnded(ProcessRecord process, int exitCode)
    {
        try
        {
            return _processManager.Complete(process.Pid, exitCode);
        }
        catch (KeyNotFoundException)
        {
            return _processManager.GetHistory().LastOrDefault(record => record.Pid == process.Pid) ?? process;
        }
    }

    /// <summary>
    /// Computes what the new process starts with (ADR 0050, section 5). A process with no parent
    /// (desktop launch, service) gets the session's login environment and the home directory; a
    /// child inherits its parent's, which the process manager copies. In both cases an explicit
    /// directory also updates <c>PWD</c>, as the shell does when it starts a command after <c>cd</c>,
    /// then the request's own variables apply on top.
    /// </summary>
    private async ValueTask<(VirtualPath? WorkingDirectory, IReadOnlyDictionary<string, string>? Environment)> ResolveStartEnvironmentAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        VirtualPath? workingDirectory = request.WorkingDirectory;
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        if (request.ParentPid is null)
        {
            AuthenticatedPrincipal principal = request.Principal;
            IReadOnlyDictionary<string, string> login = _loginEnvironment is null
                ? LoginEnvironment.Build(principal.LoginName.Value, principal.HomePath, environmentFile: null)
                : await _loginEnvironment.GetAsync(principal, cancellationToken).ConfigureAwait(false);
            foreach ((string name, string value) in login)
            {
                environment[name] = value;
            }

            workingDirectory ??= VirtualPath.Parse(principal.HomePath);
            environment["PWD"] = workingDirectory.Value.Value;
        }
        else if (workingDirectory is { } explicitDirectory)
        {
            environment["PWD"] = explicitDirectory.Value;
        }

        if (request.Environment is { } overrides)
        {
            foreach ((string name, string value) in overrides)
            {
                environment[name] = value;
            }
        }

        return (workingDirectory, environment.Count == 0 ? null : environment);
    }

    // ADR 0214 §4.2: before the process exists, as the user, in their home; the request's own variables win.
    private async ValueTask<IReadOnlyDictionary<string, string>?> GuardInstalledAppDataAsync(
        AppManifest manifest, AppLaunchRequest request, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        if (DataGuard is not { } guard || request.SystemScope || _fileSystem is null || _clock is null)
        {
            return environment;
        }

        AuthenticatedPrincipal principal = request.Principal;
        AppFileSystemGateway user = new(
            _fileSystem, _clock, OsSettingsService.CreateUserOperation(principal), [.. principal.GroupIds.Select(static id => id.ToString())]);
        IReadOnlyDictionary<string, string>? added = await guard.PrepareAsync(user, principal.HomePath, manifest, cancellationToken).ConfigureAwait(false);
        if (added is null)
        {
            return environment;
        }

        Dictionary<string, string> merged = new(added, StringComparer.Ordinal);
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            merged[name] = value;
        }

        return merged;
    }

    private AppLaunchResult StartService(AppDescriptor descriptor, ProcessRecord process, IAppExecutionContext context)
    {
        if (_remoteHost?.ShouldRunRemotely(descriptor.Manifest) == true)
        {
            return StartRemoteService(descriptor, process, context);
        }

        ServiceAppBase service = (ServiceAppBase)CreateAppInstance(descriptor);
        TrackService(descriptor, process, context);
        _processManager.MarkRunning(process.Pid);
        Task runTask = RunServiceAsync(service, process.Pid, context);

        lock (_sync)
        {
            _running[process.Pid] = new RunningInstance(descriptor, context, service, runTask);
        }

        return new AppLaunchResult(AppLaunchStatus.Launched, process, context);
    }

    /// <summary>
    /// Starts a service in a worker of its own (ADR 0070, section 3). The OS tracks it like a local
    /// one; its <c>OnStoppingAsync</c> runs in the worker.
    /// </summary>
    private AppLaunchResult StartRemoteService(AppDescriptor descriptor, ProcessRecord process, IAppExecutionContext context)
    {
        TrackService(descriptor, process, context);
        _processManager.MarkRunning(process.Pid);
        IRemoteService remote = _remoteHost!.StartService(
            new RemoteProcessLaunch(descriptor, context, ControlOf(context), PlacementGroup: null));
        Task runTask = RunServiceAsync(() => remote.Completion, process.Pid, context);

        lock (_sync)
        {
            _running[process.Pid] = new RunningInstance(descriptor, context, ServiceInstance: null, runTask, remote);
        }

        return new AppLaunchResult(AppLaunchStatus.Launched, process, context);
    }

    /// <summary>
    /// Opens a new run of a <see cref="AppKind.Service"/> app in the state table (ADR 0097): the
    /// unit is <c>activating</c> until the service's notification says it is ready, the process gets
    /// the notification channel, and a watchdog kills a start that outlives
    /// <see cref="ServiceStartTimeout"/>. A web app (same base class) is not a unit.
    /// </summary>
    private void TrackService(AppDescriptor descriptor, ProcessRecord process, IAppExecutionContext context)
    {
        if (descriptor.Manifest.Kind != AppKind.Service)
        {
            return;
        }

        string appId = descriptor.Manifest.Id;
        ProcessId pid = process.Pid;
        _servicePids[pid] = appId;
        _serviceStates.MarkActivating(appId, pid);
        if (context is AppExecutionContext trusted)
        {
            trusted.ServiceNotify = new ServiceNotifyGateway(notification =>
            {
                if (_serviceStates.Notify(appId, pid, notification) is { } health)
                {
                    _processManager.SetServiceHealth(pid, health);
                }
            });
        }

        _ = WatchServiceStartAsync(appId, pid);
    }

    /// <summary>Kills a service still <c>activating</c> after <see cref="ServiceStartTimeout"/> (ADR 0097, <c>Result: timeout</c>).</summary>
    private async Task WatchServiceStartAsync(string appId, ProcessId pid)
    {
        TimeSpan timeout = ServiceStartTimeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        // Simulated time when the OS has a clock (a paused simulation does not time a start out).
        await (_clock?.DelayAsync(timeout) ?? Task.Delay(timeout)).ConfigureAwait(false);
        if (_serviceStates.IsActivating(appId, pid) && _processManager.TryGetActive(pid, out _))
        {
            _logger.LogWarning("{Unit}: start operation timed out. Terminating.", UnitNameOf(appId));
            _serviceStates.MarkStartTimedOut(appId, pid);
            try
            {
                _processManager.Kill(pid, ProcessExitReason.Timeout);
            }
            catch (KeyNotFoundException)
            {
                // Ended concurrently.
            }
        }
    }

    private Task RunServiceAsync(ServiceAppBase service, ProcessId pid, IAppExecutionContext context) =>
        RunServiceAsync(() => service.RunAsync(context, context.CancellationToken), pid, context);

    private async Task RunServiceAsync(Func<Task> run, ProcessId pid, IAppExecutionContext context)
    {
        try
        {
            await RunAsProcessAsync(context, async () =>
            {
                await run();
                return 0;
            });
            if (_processManager.TryGetActive(pid, out _))
            {
                _processManager.Complete(pid, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected once the process token is cancelled. StopInstanceAsync (logout, shutdown,
            // disable) unregisters the instance first and ends the process itself; a SIGTERM from
            // another process (kill, End task, ADR 0069) leaves it registered: the service ended by
            // honoring its token, so it completes within its grace period. So does a service whose
            // scope was cancelled under it (the session root at logout, the system scope at
            // shutdown): its unit must not stay active with no code running (ADR 0097).
            bool stoppedByEcosystem;
            lock (_sync)
            {
                stoppedByEcosystem = !_running.ContainsKey(pid);
            }

            if (!stoppedByEcosystem && _processManager.TryGetActive(pid, out _))
            {
                try
                {
                    _processManager.Complete(pid, 0);
                }
                catch (KeyNotFoundException)
                {
                    // Ended concurrently.
                }
            }
        }
        catch
        {
            if (_processManager.TryGetActive(pid, out _))
            {
                _processManager.Fault(pid);
            }
        }
    }

    /// <summary>
    /// Runs the entry point of a headless process on its own scheduler (ADR 0069, section 3), when the
    /// context comes from the trusted factory: a killed process's pending continuations are dropped
    /// and the wait ends at once with <see cref="HackProcessKilledException"/>. Other contexts
    /// (tests) run the entry point directly.
    /// </summary>
    private static Task<T> RunAsProcessAsync<T>(IAppExecutionContext context, Func<Task<T>> entryPoint) =>
        context is AppExecutionContext trusted ? trusted.Control.RunAsync(entryPoint) : entryPoint();

    private AppLaunchResult StartWindow(AppDescriptor descriptor, ProcessRecord process, IAppExecutionContext context)
    {
        // Register the running instance before marking the process Running: MarkRunning
        // publishes ProcessStateChangedEvent synchronously, and subscribers (e.g. the Blazor
        // window runtime presenting a new window) call TryGetRunningInstance in reaction to
        // that same event, so the entry must already be visible by the time it fires.
        lock (_sync)
        {
            _running[process.Pid] = new RunningInstance(
                descriptor, context, ServiceInstance: null, RunTask: null);
        }

        if (context is AppExecutionContext trusted)
        {
            // ADR 0069: the default action of SIGTERM for a window app is to close its window,
            // once the app's own handlers (recovery draft, current operation) are done.
            ProcessId pid = process.Pid;
            trusted.Control.StopRequested += request => _ = CloseWindowAfterStopRequestAsync(pid, trusted, request);
        }

        ProcessRecord running = _processManager.MarkRunning(process.Pid);

        return new AppLaunchResult(AppLaunchStatus.Launched, running, context);
    }

    /// <summary>
    /// Closes a window app asked to stop (<c>kill</c>, "End task", <c>CloseMainWindowAsync</c>) after
    /// the work its handlers deferred, and before the deadline at which the process manager would
    /// kill it. The process then ends with the request's reason, not as killed.
    /// </summary>
    private async Task CloseWindowAfterStopRequestAsync(ProcessId pid, AppExecutionContext context, ProcessStopRequest request)
    {
        try
        {
            TimeSpan remaining = request.Deadline - context.Clock.UtcNow;
            await WaitForStopWorkAsync(context, context.Clock.DelayAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)).ConfigureAwait(false);
            await StopAsync(pid, request.Reason).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HackProcessKilledException or KeyNotFoundException or InvalidOperationException)
        {
            // Killed at the deadline, or ended meanwhile: nothing left to close.
        }
    }

    /// <summary>Waits for the work the app deferred on its stop request, or until <paramref name="limit"/> completes.</summary>
    /// <returns><see langword="false"/> when <paramref name="limit"/> came first: the work was still running.</returns>
    private static async Task<bool> WaitForStopWorkAsync(IAppExecutionContext context, Task limit)
    {
        if (context.StopRequest is { } request)
        {
            Task work = request.WhenDeferredWorkEndedAsync();
            return await Task.WhenAny(work, limit).ConfigureAwait(false) == work;
        }

        return true;
    }

    private async Task<bool> StopInstanceAsync(ProcessId pid, RunningInstance instance, ProcessExitReason reason)
    {
        lock (_sync)
        {
            _running.Remove(pid);
        }

        if (_servicePids.TryGetValue(pid, out string? serviceId))
        {
            _serviceStates.MarkDeactivating(serviceId, pid);
        }

        // Whether the app did not finish its stop work within the grace period (ADR 0097, section 5).
        bool forced = false;

        bool isService = instance.ServiceInstance is not null || instance.RemoteService is not null;
        if (_processManager.TryGetActive(pid, out _) && AnnounceStop(pid, instance.Context, reason, isService))
        {
            // Logout, shutdown, disable: the app keeps a recovery draft or ends its operation first.
            forced |= !await WaitForStopWorkAsync(instance.Context, Task.Delay(StopBudget)).ConfigureAwait(false);
        }

        if ((instance.ServiceInstance is not null || instance.RemoteService is not null) && _processManager.TryGetActive(pid, out _))
        {
            using CancellationTokenSource stopBudget = new(StopBudget);
            try
            {
                Task stopping = instance.ServiceInstance is { } service
                    ? service.StopAsync(ToServiceStopReason(reason), stopBudget.Token).AsTask()
                    : instance.RemoteService!.StopAsync(ToServiceStopReason(reason), stopBudget.Token);
                await stopping;
            }
            catch (OperationCanceledException) when (stopBudget.IsCancellationRequested)
            {
                // The service's cleanup outlived its budget: it is cut off below.
                forced = true;
            }
            catch
            {
                // Best-effort cleanup only; the process is force-stopped below regardless.
            }
        }

        if (_processManager.TryGetActive(pid, out _))
        {
            _processManager.Kill(pid, reason);
        }

        if (instance.RunTask is not null)
        {
            try
            {
                await instance.RunTask;
            }
            catch
            {
                // Faults from the run task are already reflected in the process's Faulted state.
            }
        }

        return forced;
    }

    /// <summary>Grace period an app gets to stop before its process is force-stopped.</summary>
    private static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delivers <c>StopRequested</c> to an app the OS is about to stop (ADR 0069, section 1):
    /// logout, shutdown, <c>systemctl stop</c>, app disabled, with a 5-second deadline. A
    /// <c>SIGKILL</c> (<see cref="ProcessExitReason.Killed"/>) is not announced, nor is a window the
    /// user closed (<see cref="ProcessExitReason.CloseRequested"/>): its close guard already asked
    /// about unsaved work.
    /// </summary>
    /// <returns><see langword="true"/> when the app has a stop request to honor (this one or an earlier one).</returns>
    private static bool AnnounceStop(ProcessId pid, IAppExecutionContext context, ProcessExitReason reason, bool isService)
    {
        if (context is not AppExecutionContext trusted || trusted.IsKilled)
        {
            return false;
        }

        if (reason == ProcessExitReason.Killed || (!isService && reason == ProcessExitReason.CloseRequested))
        {
            return trusted.StopRequest is not null;
        }

        DateTimeOffset now = trusted.Clock.UtcNow;
        if (trusted.Control.TryRequestStop(new ProcessStopRequest(pid, reason, now, now + StopBudget)))
        {
            trusted.Control.RaiseStopRequested();
        }

        return true;
    }

    private static ServiceStopReason ToServiceStopReason(ProcessExitReason reason) => reason switch
    {
        ProcessExitReason.Disabled => ServiceStopReason.Disabled,
        ProcessExitReason.Logout => ServiceStopReason.Logout,
        ProcessExitReason.Shutdown => ServiceStopReason.Shutdown,
        ProcessExitReason.Fault => ServiceStopReason.Faulted,
        _ => ServiceStopReason.Shutdown
    };

    /// <summary>Tracks one currently running app instance so it can be stopped later.</summary>
    private sealed record RunningInstance(
        AppDescriptor Descriptor,
        IAppExecutionContext Context,
        ServiceAppBase? ServiceInstance,
        Task? RunTask,
        IRemoteService? RemoteService = null);

    /// <summary>Maps a manifest's declared resource weights to the simulator's process profile.</summary>
    /// <summary>
    /// The <c>oom_score_adj</c> a new process of <paramref name="manifest"/> starts with (ADR 0126,
    /// section 4): the declared value, a negative one only for a system app (the generator refuses it
    /// for any other, so here it is only a manifest the build did not check, ignored and read as
    /// <c>0</c>); undeclared, <c>-500</c> for a system service and <see langword="null"/> otherwise, so
    /// the process inherits its parent's value like a <c>fork</c> (<c>0</c> without a parent).
    /// </summary>
    private int? OomScoreAdjustOf(AppManifest manifest)
    {
        bool system = _catalog.IsSystemApp(manifest.Id);
        if (manifest.OomScoreAdj is { } declared)
        {
            return system ? declared : Math.Max(declared, 0);
        }

        return system && manifest.Kind == AppKind.Service ? SystemServiceOomScoreAdjust : null;
    }

    /// <summary>The <c>oom_score_adj</c> of a system service that declares none (ADR 0126, section 4).</summary>
    internal const int SystemServiceOomScoreAdjust = -500;

    private static ResourceProfile ToResourceProfile(AppResourceProfileManifest resources) => new(
        resources.BaselineCpuWeight,
        resources.BurstCpuWeight,
        resources.BaselineMemoryWeight,
        resources.BurstMemoryWeight,
        resources.BaselineStorageIoWeight,
        resources.BurstStorageIoWeight,
        resources.BaselineNetworkIoWeight,
        resources.BurstNetworkIoWeight);
}

/// <summary>The streams of an instance started by a shell (ADR 0064).</summary>
internal sealed class AttachedAppConsole(TextReader input, TextWriter output, TextWriter error) : IAppConsoleGateway
{
    public bool IsAttached => true;

    public TextReader In { get; } = input;

    public TextWriter Out { get; } = output;

    public TextWriter Error { get; } = error;
}

/// <summary>
/// Forwards to a terminal, pipe or file, and silently drops the text once that end is gone
/// (closed terminal, broken pipe): a window app must never fault because the shell that started
/// it went away, as a GUI program ignores <c>EIO</c> on a hung-up terminal.
/// </summary>
internal sealed class ForgivingTextWriter(TextWriter inner) : TextWriter
{
    private volatile bool _broken;

    public override System.Text.Encoding Encoding => inner.Encoding;

    public override string NewLine
    {
        get => inner.NewLine;
#pragma warning disable CS8765 // Nullability matches TextWriter.NewLine's setter.
        set => inner.NewLine = value;
#pragma warning restore CS8765
    }

    public override void Write(char value) => Forward(() => inner.Write(value));

    public override void Write(string? value) => Forward(() => inner.Write(value));

    public override void WriteLine(string? value) => Forward(() => inner.WriteLine(value));

    public override Task WriteAsync(char value) => ForwardAsync(() => inner.WriteAsync(value));

    public override Task WriteAsync(string? value) => ForwardAsync(() => inner.WriteAsync(value));

    public override Task WriteLineAsync(string? value) => ForwardAsync(() => inner.WriteLineAsync(value));

    public override void Flush() => Forward(inner.Flush);

    public override Task FlushAsync() => ForwardAsync(inner.FlushAsync);

    private void Forward(Action write)
    {
        if (_broken)
        {
            return;
        }

        try
        {
            write();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _broken = true;
        }
    }

    private async Task ForwardAsync(Func<Task> write)
    {
        if (_broken)
        {
            return;
        }

        try
        {
            await write().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _broken = true;
        }
    }
}
