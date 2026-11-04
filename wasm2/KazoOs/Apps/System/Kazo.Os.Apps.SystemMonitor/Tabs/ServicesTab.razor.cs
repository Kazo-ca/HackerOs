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
/// type, and start/stop actions, on <see cref="KazoServiceController"/> (ADR 0056, section 5).
/// </summary>
/// <remarks>
/// The list and the start types are read when the tab opens and after each action. The state
/// (ADR 0097: active, activating, failed with its reason, the health and the status text the
/// service reported) is read again from the service manager each time the window refreshes its
/// processes, every second, so a service that crashes shows as failed without reloading the list.
/// </remarks>
public partial class ServicesTab
{
    private IReadOnlyList<KazoServiceController> _services = [];
    private bool _busy;
    private bool _refreshing;
    private bool _hasManagerState = true;
    private string? _message;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The tab's texts in the language of the System Monitor process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets or sets the processes of the last refresh, by PID, for the live status.</summary>
    [Parameter, EditorRequired]
    public IReadOnlyDictionary<long, KazoProcess> Processes { get; set; } = new Dictionary<long, KazoProcess>();

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
            foreach (KazoServiceController service in _services)
            {
                _hasManagerState = await service.RefreshStatusAsync();
                if (!_hasManagerState)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is KazoIOException or NotSupportedException)
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
            _services = await KazoServiceController.GetServicesAsync();
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
    private async Task RunAsync(KazoServiceController service, Func<CancellationToken, Task> action, string deniedKey)
    {
        _busy = true;
        try
        {
            await action(CancellationToken.None);
            _message = null;
        }
        catch (KazoUnauthorizedAccessException)
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

    private async Task SetStartTypeAsync(KazoServiceController service, object? value)
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
    private KazoServiceActiveState LiveState(KazoServiceController service) =>
        _hasManagerState
            ? service.ActiveState
            : FindProcess(service)?.State switch
            {
                null or KazoProcessState.Dead or KazoProcessState.Zombie => KazoServiceActiveState.Inactive,
                KazoProcessState.Idle or KazoProcessState.Sleeping => KazoServiceActiveState.Activating,
                KazoProcessState.Stopping => KazoServiceActiveState.Deactivating,
                _ => KazoServiceActiveState.Active
            };

    private long? LivePid(KazoServiceController service) =>
        _hasManagerState && service.ActiveState is KazoServiceActiveState.Inactive or KazoServiceActiveState.Failed
            ? null
            : FindProcess(service)?.Id ?? service.ProcessId;

    private KazoProcess? FindProcess(KazoServiceController service) =>
        Processes.Values.FirstOrDefault(process => string.Equals(process.AppId, service.ServiceName, StringComparison.Ordinal));

    private string StateText(KazoServiceController service, KazoServiceActiveState state) => state switch
    {
        KazoServiceActiveState.Active => L["Services.State.Running"],
        KazoServiceActiveState.Activating => L["Services.State.Starting"],
        KazoServiceActiveState.Deactivating => L["Services.State.Stopping"],
        KazoServiceActiveState.Failed => L["Services.State.Failed", KazoServiceStates.ToSystemdName(service.Result)],
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
    private string StateTitle(KazoServiceController service, KazoServiceActiveState state)
    {
        string text = $"{KazoServiceStates.ToSystemdName(state)}";
        if (state == KazoServiceActiveState.Failed)
        {
            text += $" (Result: {KazoServiceStates.ToSystemdName(service.Result)}{(service.ExitCode is { } code ? $", status={code}" : string.Empty)})";
        }

        return service.ActiveStateChangedAt is { } since
            ? L["Services.Since", text, since.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)]
            : text;
    }

    private string HealthText(KazoServiceHealth health) => health switch
    {
        KazoServiceHealth.Healthy => L["Services.Health.Healthy"],
        KazoServiceHealth.Degraded => L["Services.Health.Degraded"],
        KazoServiceHealth.Unhealthy => L["Services.Health.Unhealthy"],
        _ => L["Services.Health.Unknown"]
    };
}
