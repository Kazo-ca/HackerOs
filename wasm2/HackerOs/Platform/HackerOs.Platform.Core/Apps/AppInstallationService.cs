using HackerOs.App.Abstractions;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Apps;

/// <summary>
/// Default <see cref="IAppInstallationService"/> (ADR 0048 section 4): reads
/// <c>/etc/hackeros/apps.config</c> and the session user's <c>~/.config/hackeros/apps.config</c>,
/// watches both, and keeps <see cref="AppEnablementRegistry"/> equal to the effective state.
/// </summary>
/// <remarks>
/// <para>
/// The effective state is "machine OR user": an app listed in either file is uninstalled, and so is
/// every app that needs it through non-optional dependencies. A listed app whose dependents include
/// a system app is ignored with a warning, exactly like a listed system app, so no file edit can
/// take a system app down.
/// </para>
/// <para>
/// Every change goes through the same path, whoever made it: <see cref="ReloadAsync"/> reads both
/// files, then stops and disables the newly uninstalled apps (dependents first, through
/// <see cref="AppLifecycleOrchestrator"/>) and re-enables the reinstalled ones (dependencies first).
/// </para>
/// </remarks>
public sealed class AppInstallationService : IAppInstallationService, IAsyncDisposable
{
    private readonly AppCatalog _catalog;
    private readonly AppEnablementRegistry _enablement;
    private readonly AppLifecycleOrchestrator _orchestrator;
    private readonly IFileSystemService _fileSystem;
    private readonly ISessionService _session;
    private readonly ISimulationClock _clock;
    private readonly ITopicMessageBus? _topicBus;
    private readonly ILogger _logger;
    private readonly WebData.IWebSiteDatabaseStore? _webDatabases;
    private readonly IDisposable[] _sessionSubscriptions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _loggedWarnings = new(StringComparer.Ordinal);
    private readonly List<SettingsFilesWatcher> _watchers = [];
    private readonly object _sync = new();

    private IReadOnlySet<string> _machineListed = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlySet<string> _userListed = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlySet<string> _machineEffective = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlySet<string> _effective = new HashSet<string>(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>Creates the service; call <see cref="StartAsync"/> once storage is ready.</summary>
    /// <param name="catalog">Catalog of this build, with its system app list.</param>
    /// <param name="enablement">Launch checkpoint kept equal to the effective state.</param>
    /// <param name="orchestrator">Stops running instances of newly uninstalled apps.</param>
    /// <param name="fileSystem">Filesystem engine; every access is authorized there.</param>
    /// <param name="session">Session service giving the user whose file applies.</param>
    /// <param name="clock">Simulation clock for authorization timestamps.</param>
    /// <param name="topicBus">Optional topic bus enabling the file watchers.</param>
    /// <param name="eventBus">Optional kernel event bus; login and logout then reload the user layer.</param>
    /// <param name="logger">Optional logger; its lines reach <c>/var/log/syslog</c> (ADR 0045).</param>
    /// <param name="webDatabases">
    /// Browser databases of web sites: <see cref="UninstallAsync"/> deletes the one of each uninstalled
    /// web app (ADR 0088, section 4); <see langword="null"/> deletes nothing.
    /// </param>
    public AppInstallationService(
        AppCatalog catalog,
        AppEnablementRegistry enablement,
        AppLifecycleOrchestrator orchestrator,
        IFileSystemService fileSystem,
        ISessionService session,
        ISimulationClock clock,
        ITopicMessageBus? topicBus = null,
        IEventBus? eventBus = null,
        ILogger<AppInstallationService>? logger = null,
        WebData.IWebSiteDatabaseStore? webDatabases = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _topicBus = topicBus;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _webDatabases = webDatabases;
        _sessionSubscriptions = eventBus is null
            ? []
            :
            [
                eventBus.Subscribe<SessionActivatedEvent>(_ => { Task ignored = OnSessionChangedAsync(); }),
                eventBus.Subscribe<SessionLoggedOutEvent>(_ => { Task ignored = OnSessionChangedAsync(); })
            ];
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>
    /// Gets the apps installed from packages at boot (ADR 0189 §3): another user's personal apps are
    /// treated as uninstalled for this session. <see langword="null"/> hides nothing.
    /// </summary>
    public Packages.InstalledPackageCatalog? InstalledPackages { get; init; }

    /// <summary>Reads both files, applies them and starts watching them. Called at boot; idempotent.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        await RewatchAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public AppInstallationState GetState(string appId)
    {
        RequireKnown(appId);
        if (_catalog.IsSystemApp(appId))
        {
            return AppInstallationState.System;
        }

        lock (_sync)
        {
            return _machineEffective.Contains(appId) ? AppInstallationState.UninstalledForMachine
                : _effective.Contains(appId) ? AppInstallationState.UninstalledForUser
                : AppInstallationState.Installed;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetUninstallClosure(string appId)
    {
        RequireKnown(appId);
        HashSet<string> closure = AppsConfigDocument.ExpandDependents(_catalog, [appId]);
        return [.. _catalog.DeactivationOrder.Where(closure.Contains)];
    }

    /// <inheritdoc />
    public ValueTask<AppUninstallPlan> PlanUninstallAsync(string appId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> closure = GetUninstallClosure(appId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AppUninstallPlan(closure, _orchestrator.GetRunningProcesses(closure)));
    }

    /// <inheritdoc />
    public async ValueTask<AppInstallationResult> UninstallAsync(
        string appId,
        AppInstallationScope scope,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!_catalog.Manifests.ContainsKey(appId))
        {
            return AppInstallationResult.Failed($"apps.app.not-found:{appId}");
        }

        IReadOnlyList<string> closure = GetUninstallClosure(appId);
        string? system = closure.FirstOrDefault(_catalog.IsSystemApp);
        if (system is not null)
        {
            return AppInstallationResult.Failed(system == appId
                ? $"apps.app.system:{appId}"
                : $"apps.closure.system-app:{system}");
        }

        if (!TryResolveTarget(scope, writer, out VirtualPath path, out IAppFileSystemGateway? gateway, out string defaultContent))
        {
            return AppInstallationResult.Failed("apps.session.required");
        }

        // Taken before the edit: the file watcher may apply the new file (and stop them) before
        // the reload below, which then has nothing left to stop (ADR 0097, section 5).
        IReadOnlyList<AppProcessInfo> runningBefore = _orchestrator.GetRunningProcesses(closure);
        await EditAsync(gateway, path, content => AppsConfigDocument.MarkUninstalled(content, closure, defaultContent), createIfMissing: true, cancellationToken)
            .ConfigureAwait(false);
        foreach (string uninstalled in closure)
        {
            _logger.LogInformation("apps: {AppId} uninstalled (scope={Scope}, by={User})", uninstalled, ScopeName(scope), ActorName());
        }

        IReadOnlyList<AppProcessInfo> reloaded = await ReloadCoreAsync(cancellationToken).ConfigureAwait(false);
        HashSet<long> reported = [.. reloaded.Select(process => process.Pid)];
        IReadOnlyList<AppProcessInfo> stopped =
        [
            .. reloaded,
            .. runningBefore.Where(process => !reported.Contains(process.Pid)
                && !_orchestrator.TryGetRunningInstance(ProcessId.FromInt64(process.Pid), out _, out _))
        ];
        foreach (AppProcessInfo process in stopped)
        {
            _logger.LogInformation(
                "apps: process {Pid} ({Name}, user {User}) stopped by the uninstall{Forced}",
                process.Pid, process.Name, process.UserName, process.Forced ? ", forced after its grace period" : string.Empty);
        }

        await DeleteWebSiteDatabasesAsync(closure, cancellationToken).ConfigureAwait(false);
        return new AppInstallationResult(true, closure, [], stopped);
    }

    /// <summary>
    /// Deletes the browser database of every web app just uninstalled (ADR 0088, section 4): the only data
    /// an uninstall removes, since the user cannot reach it in the files. A failure is logged, not thrown:
    /// the uninstall itself is done.
    /// </summary>
    private async ValueTask DeleteWebSiteDatabasesAsync(IReadOnlyList<string> uninstalled, CancellationToken cancellationToken)
    {
        if (_webDatabases is null)
        {
            return;
        }

        foreach (string appId in uninstalled)
        {
            if (!_catalog.Manifests.TryGetValue(appId, out AppManifest? manifest) || manifest.Kind != AppKind.Web)
            {
                continue;
            }

            string name = AppSdk.WebData.HackWebDbNames.ForApp(appId);
            try
            {
                await _webDatabases.DeleteAsync(name, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("apps: {AppId} browser database {Database} deleted", appId, name);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning("apps: {AppId} browser database {Database} could not be deleted: {Error}", appId, name, exception.Message);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<AppInstallationResult> ReinstallAsync(
        string appId,
        AppInstallationScope scope,
        IAppFileSystemGateway? writer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!_catalog.Manifests.ContainsKey(appId))
        {
            return AppInstallationResult.Failed($"apps.app.not-found:{appId}");
        }

        HashSet<string> needed = AppsConfigDocument.ExpandDependencies(_catalog, appId);
        IReadOnlySet<string> listed;
        lock (_sync)
        {
            if (scope == AppInstallationScope.User && needed.Any(_machineEffective.Contains))
            {
                return AppInstallationResult.Failed($"apps.uninstalled-by-administrator:{appId}");
            }

            listed = scope == AppInstallationScope.Machine ? _machineListed : _userListed;
        }

        string[] toRemove = [.. _catalog.ActivationOrder.Where(id => needed.Contains(id) && listed.Contains(id))];
        if (toRemove.Length == 0)
        {
            return new AppInstallationResult(true, [], []);
        }

        if (!TryResolveTarget(scope, writer, out VirtualPath path, out IAppFileSystemGateway? gateway, out _))
        {
            return AppInstallationResult.Failed("apps.session.required");
        }

        await EditAsync(gateway, path, content => AppsConfigDocument.MarkInstalled(content, toRemove), createIfMissing: false, cancellationToken)
            .ConfigureAwait(false);
        foreach (string reinstalled in toRemove)
        {
            _logger.LogInformation("apps: {AppId} reinstalled (scope={Scope}, by={User})", reinstalled, ScopeName(scope), ActorName());
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return new AppInstallationResult(true, toRemove, []);
    }

    /// <summary>
    /// Re-reads both files and applies the effective state: newly uninstalled apps are stopped and
    /// disabled, reinstalled ones enabled again. A missing or unreadable file means "nothing uninstalled".
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask ReloadAsync(CancellationToken cancellationToken = default) =>
        await ReloadCoreAsync(cancellationToken).ConfigureAwait(false);

    /// <summary><see cref="ReloadAsync"/>, returning the processes the newly uninstalled apps had running.</summary>
    private async ValueTask<IReadOnlyList<AppProcessInfo>> ReloadCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AppProcessInfo> stopped;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
            AppsConfigReadResult machine = await ReadFileAsync(CreateGateway(OsSettingsService.CreateSystemOperation(), ["system"]), AppsConfigDocument.MachinePath, cancellationToken)
                .ConfigureAwait(false);
            AppsConfigReadResult user = principal is null
                ? AppsConfigReadResult.Empty
                : await ReadFileAsync(CreateUserGateway(principal), AppsConfigDocument.GetUserPath(principal.HomePath), cancellationToken)
                    .ConfigureAwait(false);

            HashSet<string> machineEffective = ExpandGuarded(machine.UninstalledAppIds, AppsConfigDocument.MachinePath);
            HashSet<string> effective = ExpandGuarded(
                machine.UninstalledAppIds.Concat(user.UninstalledAppIds),
                principal is null ? AppsConfigDocument.MachinePath : AppsConfigDocument.GetUserPath(principal.HomePath));
            // ADR 0189 §3: another user's personal apps are not installed for this session.
            effective.UnionWith(AppsConfigDocument.ExpandDependents(_catalog, InstalledPackages?.GetHiddenFor(principal?.HomePath) ?? new HashSet<string>()));

            lock (_sync)
            {
                _machineListed = machine.UninstalledAppIds;
                _userListed = user.UninstalledAppIds;
                _machineEffective = machineEffective;
                _effective = effective;
            }

            stopped = await ApplyAsync(effective).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return stopped;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (IDisposable subscription in _sessionSubscriptions)
        {
            subscription.Dispose();
        }

        await DisposeWatchersAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Expands listed apps with their dependents, dropping (with a warning) any listed app whose
    /// expansion would reach a system app.
    /// </summary>
    private HashSet<string> ExpandGuarded(IEnumerable<string> listed, VirtualPath path)
    {
        List<string> accepted = [];
        foreach (string appId in listed.Distinct(StringComparer.Ordinal))
        {
            string? system = AppsConfigDocument.ExpandDependents(_catalog, [appId]).FirstOrDefault(_catalog.IsSystemApp);
            if (system is null)
            {
                accepted.Add(appId);
            }
            else
            {
                LogWarningOnce($"apps: {path.Value}: {appId} is needed by system app {system} and cannot be uninstalled, line ignored");
            }
        }

        return AppsConfigDocument.ExpandDependents(_catalog, accepted);
    }

    private async ValueTask<IReadOnlyList<AppProcessInfo>> ApplyAsync(IReadOnlySet<string> effective)
    {
        List<AppProcessInfo> stopped = [];
        // Dependents first, so each DisableAsync only has its own app left to stop.
        foreach (string appId in _catalog.DeactivationOrder)
        {
            if (effective.Contains(appId) && _enablement.IsEnabled(appId))
            {
                AppDisableResult disabled = await _orchestrator.DisableAsync(appId, ProcessExitReason.Disabled).ConfigureAwait(false);
                stopped.AddRange(disabled.StoppedProcesses ?? []);
            }
        }

        // Dependencies first, so a re-enable never finds a dependency still disabled.
        foreach (string appId in _catalog.ActivationOrder)
        {
            if (!effective.Contains(appId) && !_enablement.IsEnabled(appId))
            {
                await _orchestrator.EnableAsync(appId).ConfigureAwait(false);
            }
        }

        return stopped;
    }

    private async ValueTask<AppsConfigReadResult> ReadFileAsync(IAppFileSystemGateway gateway, VirtualPath path, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(gateway, path, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            LogWarningOnce($"apps: cannot read {path.Value}: {error.Code}, nothing uninstalled by it");
            return AppsConfigReadResult.Empty;
        }

        AppsConfigReadResult result = AppsConfigDocument.Read(read.Content, path, _catalog);
        foreach (string warning in result.Warnings)
        {
            LogWarningOnce(warning);
        }

        return result;
    }

    private static async ValueTask EditAsync(
        IAppFileSystemGateway gateway,
        VirtualPath path,
        Func<string?, string> edit,
        bool createIfMissing,
        CancellationToken cancellationToken)
    {
        FileSystemError? error = await SettingsTextFile.EditAsync(gateway, path, edit, createIfMissing, cancellationToken).ConfigureAwait(false);
        if (error is null || (error.Code == FileSystemErrorCode.NotFound && !createIfMissing))
        {
            return;
        }

        throw error.Code switch
        {
            FileSystemErrorCode.PermissionDenied
                or FileSystemErrorCode.CapabilityDenied
                or FileSystemErrorCode.AuthorityDenied
                or FileSystemErrorCode.ProtectedEntry => new HackUnauthorizedAccessException(
                    $"Access to the path '{path.Value}' is denied ({error.Code}).", error.Code, path.Value, error.RequiredCapability),
            FileSystemErrorCode.Cancelled => new OperationCanceledException(),
            _ => new HackIOException($"The operation on '{path.Value}' failed ({error.Code}).", error.Code, path.Value)
        };
    }

    private bool TryResolveTarget(
        AppInstallationScope scope,
        IAppFileSystemGateway? writer,
        out VirtualPath path,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IAppFileSystemGateway? gateway,
        out string defaultContent)
    {
        AuthenticatedPrincipal? principal = _session.CurrentPrincipal;
        if (scope == AppInstallationScope.Machine)
        {
            path = AppsConfigDocument.MachinePath;
            defaultContent = AppsConfigDocument.MachineDefaultContent;
            // The file's own permissions decide (system:system 0644): with no caller gateway, the
            // session user writes as themselves; with no session at all, nobody may write.
            gateway = writer ?? (principal is null ? null : CreateUserGateway(principal));
            return gateway is not null;
        }

        path = principal is null ? AppsConfigDocument.MachinePath : AppsConfigDocument.GetUserPath(principal.HomePath);
        defaultContent = AppsConfigDocument.UserDefaultContent;
        gateway = principal is null ? null : CreateUserGateway(principal);
        return gateway is not null;
    }

    private async Task OnSessionChangedAsync()
    {
        try
        {
            // The user layer now points at another home (or at none after a logout).
            await ReloadAsync().ConfigureAwait(false);
            await RewatchAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "apps: could not reload apps.config after a session change");
        }
    }

    private async ValueTask RewatchAsync(CancellationToken cancellationToken)
    {
        if (_topicBus is null || _disposed)
        {
            return;
        }

        await DisposeWatchersAsync().ConfigureAwait(false);
        List<SettingsFilesWatcher> started = [];
        SettingsFilesWatcher machine = new(
            CreateWatchGateway(OsSettingsService.CreateSystemOperation(), ["system"]),
            [AppsConfigDocument.MachinePath],
            OnFileChangedAsync);
        await machine.StartAsync(cancellationToken).ConfigureAwait(false);
        started.Add(machine);

        if (_session.CurrentPrincipal is { } principal)
        {
            SettingsFilesWatcher user = new(
                CreateWatchGateway(OsSettingsService.CreateUserOperation(principal), GroupsOf(principal)),
                [AppsConfigDocument.GetUserPath(principal.HomePath)],
                OnFileChangedAsync);
            await user.StartAsync(cancellationToken).ConfigureAwait(false);
            started.Add(user);
        }

        lock (_sync)
        {
            _watchers.AddRange(started);
        }
    }

    private async ValueTask OnFileChangedAsync()
    {
        try
        {
            await ReloadAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning(exception, "apps: could not apply a change to apps.config");
        }
    }

    private async ValueTask DisposeWatchersAsync()
    {
        SettingsFilesWatcher[] watchers;
        lock (_sync)
        {
            watchers = [.. _watchers];
            _watchers.Clear();
        }

        foreach (SettingsFilesWatcher watcher in watchers)
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RequireKnown(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!_catalog.Manifests.ContainsKey(appId))
        {
            throw new ArgumentException($"App '{appId}' is not in the catalog.", nameof(appId));
        }
    }

    private void LogWarningOnce(string line)
    {
        bool first;
        lock (_sync)
        {
            first = _loggedWarnings.Add(line);
        }

        if (first)
        {
            _logger.LogWarning("{AppsWarning}", line);
        }
    }

    private string ActorName() => _session.CurrentPrincipal?.LoginName.Value ?? "system";

    private static string ScopeName(AppInstallationScope scope) => scope == AppInstallationScope.Machine ? "machine" : "user";

    private static string[] GroupsOf(AuthenticatedPrincipal principal) =>
        [.. principal.GroupIds.Select(static id => id.ToString())];

    private IAppFileSystemGateway CreateUserGateway(AuthenticatedPrincipal principal) =>
        CreateGateway(OsSettingsService.CreateUserOperation(principal), GroupsOf(principal));

    private IAppFileSystemGateway CreateGateway(AppOperationContext operation, IReadOnlyList<string> groups) =>
        new AppFileSystemGateway(_fileSystem, _clock, operation, groups);

    private IAppFileSystemWatchGateway CreateWatchGateway(AppOperationContext operation, IReadOnlyList<string> groups) =>
        new AppFileSystemWatchGateway(_fileSystem, _topicBus!, _clock, operation, groups, "app-installation");
}
