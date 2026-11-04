using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.Diagnostics;
using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.AppSdk.ServiceProcess;

/// <summary>
/// One background service of the OS, modeled on <c>System.ServiceProcess.ServiceController</c>
/// (ADR 0056, section 5). Replaces <c>AppCatalog</c> filtered on <see cref="AppKind.Service"/> and
/// the lifecycle orchestrator, which apps may no longer inject (ADR 0055).
/// </summary>
/// <example>
/// <code>
/// foreach (HackServiceController service in await HackServiceController.GetServicesAsync())
/// {
///     Console.WriteLine($"{service.DisplayName}: {service.Status} ({service.StartType})");
/// }
///
/// HackServiceController sync = (await HackServiceController.GetServicesAsync())[0];
/// await sync.StartAsync();
/// await sync.SetStartTypeAsync(ServiceStartMode.Automatic);
/// </code>
/// </example>
/// <remarks>
/// <para>
/// The list comes from <see cref="HackApps.GetInstalledAsync"/> (installed services only, ADR 0048),
/// the state from the OS's service manager (ADR 0097): <see cref="ActiveState"/>,
/// <see cref="Result"/>, <see cref="Health"/> and <see cref="StatusText"/>, like
/// <c>systemctl status</c>; <see cref="Status"/> follows <see cref="ActiveState"/>. A host that keeps no
/// service state falls back to <c>/proc</c> (<see cref="HackProcess"/>), where a service never reads as
/// failed.
/// </para>
/// <para>
/// Listing and reading the state need no capability. <see cref="StartAsync"/>, <see cref="StopAsync"/>,
/// <see cref="ResetFailedAsync"/>, <see cref="SetStartTypeAsync"/> and reading the start type need
/// <c>services.manage</c>, except for
/// a companion service shipped in the caller's own assembly (rule of
/// <see cref="IAppServiceControlGateway"/>).
/// </para>
/// <para>
/// Like <c>ServiceController</c>, the values are a snapshot: <see cref="RefreshAsync"/> reads them again.
/// </para>
/// </remarks>
public sealed class HackServiceController
{
    private HackServiceController(HackAppInfo app) =>
        (ServiceName, DisplayName, Description) = (app.Id, app.Name, app.Description);

    /// <summary>Gets the service name: the app id (<c>org.hackeros.sync</c>).</summary>
    public string ServiceName { get; }

    /// <summary>Gets the display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the one-line description of the service.</summary>
    public string Description { get; }

    /// <summary>Gets the status at the last read.</summary>
    public HackServiceControllerStatus Status { get; private set; } = HackServiceControllerStatus.Stopped;

    /// <summary>
    /// Gets the start type at the last read, or <see langword="null"/> when the app may not read it
    /// (another app's service without <c>services.manage</c>).
    /// </summary>
    public ServiceStartMode? StartType { get; private set; }

    /// <summary>Gets the PID of the running service process, or <see langword="null"/> when stopped.</summary>
    public long? ProcessId { get; private set; }

    /// <summary>
    /// Gets the activation state at the last read (ADR 0097, section 1): <c>failed</c> stays until the
    /// next start or <see cref="ResetFailedAsync"/>. <see cref="Status"/> is derived from it.
    /// </summary>
    public HackServiceActiveState ActiveState { get; private set; }

    /// <summary>Gets why the last run ended (<see cref="HackServiceResult.Success"/> while it runs).</summary>
    public HackServiceResult Result { get; private set; }

    /// <summary>Gets the health the service reported (ADR 0097, section 2).</summary>
    public HackServiceHealth Health { get; private set; }

    /// <summary>Gets the status text the service gave with its health, if any.</summary>
    public string? StatusText { get; private set; }

    /// <summary>Gets when <see cref="ActiveState"/> last changed (simulated time), if the service ever ran this boot.</summary>
    public DateTimeOffset? ActiveStateChangedAt { get; private set; }

    /// <summary>Gets the exit code of the last run, when its process reported one.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Gets every installed service, sorted by display name, with its status and start type.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static async Task<HackServiceController[]> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HackAppInfo> installed = await HackApps.GetInstalledAsync(cancellationToken).ConfigureAwait(false);
        HackServiceController[] services =
        [
            .. installed
                .Where(static app => app.Kind == AppKind.Service)
                .OrderBy(static app => app.Name, StringComparer.OrdinalIgnoreCase)
                .Select(static app => new HackServiceController(app))
        ];
        if (services.Length == 0)
        {
            return services;
        }

        // /proc is read once, and only when the host keeps no service state (ADR 0097).
        Task<HackProcess[]>? processes = null;
        Func<Task<HackProcess[]>> readProcesses = () => processes ??= ReadProcessesAsync(cancellationToken);
        foreach (HackServiceController service in services)
        {
            await service.LoadAsync(readProcesses, cancellationToken).ConfigureAwait(false);
        }

        return services;
    }

    /// <summary>Reads the status and start type again.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default) =>
        await LoadAsync(() => ReadProcessesAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

    /// <summary>Starts the service; does nothing when it already runs.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>services.manage</c>.</exception>
    /// <exception cref="InvalidOperationException">The service is disabled, uninstalled, or failed to start.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ServiceControlResult result = await CallAsync(gateway => gateway.StartAsync(ServiceName, cancellationToken)).ConfigureAwait(false);
        ThrowIfFailed(result, "start");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the service; does nothing when it is already stopped.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>services.manage</c>.</exception>
    /// <exception cref="InvalidOperationException">The service is uninstalled or failed to stop.</exception>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ServiceControlResult result = await CallAsync(gateway => gateway.StopAsync(ServiceName, cancellationToken)).ConfigureAwait(false);
        ThrowIfFailed(result, "stop");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the <see cref="HackServiceActiveState.Failed"/> state, like <c>systemctl reset-failed</c>;
    /// does nothing when the service is not failed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>services.manage</c>.</exception>
    /// <exception cref="InvalidOperationException">The service is uninstalled.</exception>
    public async Task ResetFailedAsync(CancellationToken cancellationToken = default)
    {
        ServiceControlResult result = await CallAsync(gateway => gateway.ResetFailedAsync(ServiceName, cancellationToken)).ConfigureAwait(false);
        ThrowIfFailed(result, "reset");
        await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads only the state kept by the service manager again (state, result, health, PID), without
    /// reading <c>/proc</c> or the start type: cheap enough for a view refreshed every second.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when the OS reported the state; <see langword="false"/> on a host that does not track it.</returns>
    public async Task<bool> RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        ServiceStatusSnapshot? status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is null)
        {
            return false;
        }

        Apply(status);
        return true;
    }

    /// <summary>Changes how the service starts, like <c>systemctl enable</c> / <c>disable</c>.</summary>
    /// <param name="startType">The new start type.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="HackUnauthorizedAccessException">This app lacks <c>services.manage</c>.</exception>
    /// <exception cref="InvalidOperationException">The service is uninstalled.</exception>
    public async Task SetStartTypeAsync(ServiceStartMode startType, CancellationToken cancellationToken = default)
    {
        ServiceControlResult result = await CallAsync(gateway => gateway.SetStartModeAsync(ServiceName, startType, cancellationToken)).ConfigureAwait(false);
        ThrowIfFailed(result, "configure");
        StartType = startType;
    }

    /// <inheritdoc />
    public override string ToString() => ServiceName;

    private async Task LoadAsync(Func<Task<HackProcess[]>> readProcesses, CancellationToken cancellationToken)
    {
        if (await ReadStatusAsync(cancellationToken).ConfigureAwait(false) is { } status)
        {
            Apply(status);
        }
        else
        {
            // A host that predates ADR 0097: the status comes from /proc alone, and nothing is ever "failed".
            HackProcess[] processes = await readProcesses().ConfigureAwait(false);
            HackProcess? process = processes.FirstOrDefault(process => string.Equals(process.AppId, ServiceName, StringComparison.Ordinal));
            ProcessId = process?.Id;
            ActiveState = process?.State switch
            {
                null or HackProcessState.Dead or HackProcessState.Zombie => HackServiceActiveState.Inactive,
                HackProcessState.Idle or HackProcessState.Sleeping => HackServiceActiveState.Activating,
                HackProcessState.Stopping => HackServiceActiveState.Deactivating,
                _ => HackServiceActiveState.Active
            };
            Status = ToStatus(ActiveState);
        }

        try
        {
            StartType = await HackExecutionContext.Current.Services.GetStartModeAsync(ServiceName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            // Reading another app's start type is a services.manage operation: unknown, not an error.
            StartType = null;
        }
    }

    private void Apply(ServiceStatusSnapshot status)
    {
        ActiveState = HackServiceStates.ToPublic(status.ActiveState);
        Result = HackServiceStates.ToPublic(status.Result);
        Health = HackServiceStates.ToPublic(status.Health);
        StatusText = status.StatusText;
        ActiveStateChangedAt = status.StateChangedAtUtc;
        ExitCode = status.ExitCode;
        ProcessId = status.MainPid;
        Status = ToStatus(ActiveState);
    }

    /// <summary>ADR 0097: the <c>ServiceController</c>-style status follows the active state; <c>failed</c> is stopped.</summary>
    private static HackServiceControllerStatus ToStatus(HackServiceActiveState state) => state switch
    {
        HackServiceActiveState.Activating => HackServiceControllerStatus.StartPending,
        HackServiceActiveState.Active => HackServiceControllerStatus.Running,
        HackServiceActiveState.Deactivating => HackServiceControllerStatus.StopPending,
        _ => HackServiceControllerStatus.Stopped
    };

    /// <summary>Reads the service manager's state, or <see langword="null"/> when the host does not keep one.</summary>
    private async Task<ServiceStatusSnapshot?> ReadStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await HackExecutionContext.Current.Services.GetStatusAsync(ServiceName, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads the processes for the status; <c>/proc</c> unreadable means "nothing visible".</summary>
    private static async Task<HackProcess[]> ReadProcessesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await HackProcess.GetProcessesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HackIOException or HackUnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<ServiceControlResult> CallAsync(Func<IAppServiceControlGateway, ValueTask<ServiceControlResult>> call)
    {
        IAppServiceControlGateway gateway = HackExecutionContext.Current.Services;
        try
        {
            return await call(gateway).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw HackGatewayErrors.ToUnauthorized(exception);
        }
    }

    private void ThrowIfFailed(ServiceControlResult result, string verb)
    {
        string? reason = result.Outcome switch
        {
            ServiceControlOutcome.Succeeded => null,
            ServiceControlOutcome.NotFound => "it is not installed",
            ServiceControlOutcome.NotAService => "it is not a service",
            ServiceControlOutcome.ServiceDisabled => "it is disabled",
            _ => $"it failed ({result.ErrorCode ?? "unknown error"})"
        };
        if (reason is not null)
        {
            throw new InvalidOperationException($"Cannot {verb} service '{ServiceName}': {reason}.");
        }
    }
}

/// <summary>
/// Status of a <see cref="HackServiceController"/>, with the values of
/// <c>System.ServiceProcess.ServiceControllerStatus</c> that KazoOS produces.
/// </summary>
public enum HackServiceControllerStatus
{
    /// <summary>No process runs the service.</summary>
    Stopped = 1,

    /// <summary>The service process is created or starting.</summary>
    StartPending = 2,

    /// <summary>The service process was asked to stop and is finishing.</summary>
    StopPending = 3,

    /// <summary>The service process runs.</summary>
    Running = 4
}
