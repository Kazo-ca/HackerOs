using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.ServiceProcess;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.SystemMonitor.Tabs;

/// <summary>
/// The Services tab (ADR 0058, section 2): every installed service with its state, health, start
/// type, and start/stop actions, on <see cref="HackServiceController"/> (ADR 0056, section 5).
/// </summary>
/// <remarks>
/// The list and the start types are read when the tab opens and after each action. The state
/// (ADR 0097: active, activating, failed with its reason, the health and the status text the
/// service reported) is read again from the service manager each time the window refreshes its
/// processes, every second, so a service that crashes shows as failed without reloading the list.
/// </remarks>
public partial class ServicesTab
{
    private IReadOnlyList<HackServiceController> _services = [];
    private bool _busy;
    private bool _refreshing;
    private bool _hasManagerState = true;
    private string? _message;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The tab's texts in the language of the System Monitor process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets or sets the processes of the last refresh, by PID, for the live status.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyDictionary<long, HackProcess> Processes { get; set; } = new Dictionary<long, HackProcess>();

    /// <inheritdoc />
    protected override Task OnInitializedAsync() => LoadAsync();

    /// <inheritdoc />
    /// <remarks>The processes changed (the window's one-second refresh): read the service states again, cheaply.</remarks>
    protected override async Task OnParametersSetAsync()
    {
        if (_busy || _refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            foreach (HackServiceController service in _services)
            {
                _hasManagerState = await service.RefreshStatusAsync();
                if (!_hasManagerState)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is HackIOException or NotSupportedException)
        {
            _hasManagerState = false;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task LoadAsync()
    {
        _busy = true;
        try
        {
            _services = await HackServiceController.GetServicesAsync();
        }
        catch (NotSupportedException)
        {
            // A host without the catalog gateway (a test harness): no service to show.
            _services = [];
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Runs one action on a service; <paramref name="deniedKey"/> names the message shown when it is refused.</summary>
    private async Task RunAsync(HackServiceController service, Func<CancellationToken, Task> action, string deniedKey)
    {
        _busy = true;
        try
        {
            await action(CancellationToken.None);
            _message = null;
        }
        catch (HackUnauthorizedAccessException)
        {
            _message = L[deniedKey, service.DisplayName];
        }
        catch (InvalidOperationException exception)
        {
            _message = exception.Message;
        }
        finally
        {
            _busy = false;
        }

        await LoadAsync();
    }

    private async Task SetStartTypeAsync(HackServiceController service, object? value)
    {
        if (value is string text && Enum.TryParse(text, out ServiceStartMode mode))
        {
            await RunAsync(service, cancellationToken => service.SetStartTypeAsync(mode, cancellationToken), "Services.Error.Configure");
        }
    }

    /// <summary>
    /// The state to show: the service manager's (refreshed every second); on a host that keeps none,
    /// derived from the processes read this second, where nothing is ever failed.
    /// </summary>
    private HackServiceActiveState LiveState(HackServiceController service) =>
        _hasManagerState
            ? service.ActiveState
            : FindProcess(service)?.State switch
            {
                null or HackProcessState.Dead or HackProcessState.Zombie => HackServiceActiveState.Inactive,
                HackProcessState.Idle or HackProcessState.Sleeping => HackServiceActiveState.Activating,
                HackProcessState.Stopping => HackServiceActiveState.Deactivating,
                _ => HackServiceActiveState.Active
            };

    private long? LivePid(HackServiceController service) =>
        _hasManagerState && service.ActiveState is HackServiceActiveState.Inactive or HackServiceActiveState.Failed
            ? null
            : FindProcess(service)?.Id ?? service.ProcessId;

    private HackProcess? FindProcess(HackServiceController service) =>
        Processes.Values.FirstOrDefault(process => string.Equals(process.AppId, service.ServiceName, StringComparison.Ordinal));

    private string StateText(HackServiceController service, HackServiceActiveState state) => state switch
    {
        HackServiceActiveState.Active => L["Services.State.Running"],
        HackServiceActiveState.Activating => L["Services.State.Starting"],
        HackServiceActiveState.Deactivating => L["Services.State.Stopping"],
        HackServiceActiveState.Failed => L["Services.State.Failed", HackServiceStates.ToSystemdName(service.Result)],
        _ => L["Services.State.Stopped"]
    };

    /// <summary>A start mode as shown in the list (the option's value stays the enum name).</summary>
    private string StartModeLabel(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => L["StartMode.Automatic"],
        ServiceStartMode.Manual => L["StartMode.Manual"],
        ServiceStartMode.Disabled => L["StartMode.Disabled"],
        _ => mode.ToString()
    };

    /// <summary>The state's tooltip: systemd's own words (<c>failed (Result: exit-code, status=1)</c>), then since when.</summary>
    private string StateTitle(HackServiceController service, HackServiceActiveState state)
    {
        string text = $"{HackServiceStates.ToSystemdName(state)}";
        if (state == HackServiceActiveState.Failed)
        {
            text += $" (Result: {HackServiceStates.ToSystemdName(service.Result)}{(service.ExitCode is { } code ? $", status={code}" : string.Empty)})";
        }

        return service.ActiveStateChangedAt is { } since
            ? L["Services.Since", text, since.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)]
            : text;
    }

    private string HealthText(HackServiceHealth health) => health switch
    {
        HackServiceHealth.Healthy => L["Services.Health.Healthy"],
        HackServiceHealth.Degraded => L["Services.Health.Degraded"],
        HackServiceHealth.Unhealthy => L["Services.Health.Unhealthy"],
        _ => L["Services.Health.Unknown"]
    };
}
