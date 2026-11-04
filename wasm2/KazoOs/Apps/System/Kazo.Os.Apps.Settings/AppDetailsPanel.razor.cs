using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.ServiceProcess;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Application (ADR 0096, section 4): the page of one installed app, shown for
/// <c>ShowAppSettingsIntent</c> when the app has no settings page of its own.
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055): <see cref="HackApps"/> for the identity, declared
/// capabilities and file handlers; <see cref="HackPermissionAdministration"/> for the state of the
/// dangerous ones; <see cref="HackServiceController"/> (capabilities <c>process.list</c> and
/// <c>services.manage</c>) for a service's state and start mode; <see cref="InstalledAppsPanel"/>
/// for uninstalling.
/// </remarks>
public sealed partial class AppDetailsPanel
{
    private HackAppInfo? _app;
    private HackAppPermissionInfo? _permissions;
    private HackServiceController? _service;
    private string? _serviceMessage;
    private bool _loaded;
    private bool _isBusy;
    private string? _shownAppId;

    /// <summary>Gets or sets the id of the app whose page is shown.</summary>
    [Parameter]
    [EditorRequired]
    public string AppId { get; set; } = string.Empty;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (_shownAppId == AppId)
        {
            return;
        }

        _shownAppId = AppId;
        _loaded = false;
        _app = null;
        _permissions = null;
        _service = null;
        _serviceMessage = null;
        IReadOnlyList<HackAppInfo> installed = await HackApps.GetInstalledAsync(Context.CancellationToken);
        _app = installed.FirstOrDefault(app => string.Equals(app.Id, AppId, StringComparison.Ordinal));
        _loaded = true;
        if (_app is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<HackAppPermissionInfo> overview = await HackPermissionAdministration.GetOverviewAsync(Context, Context.CancellationToken);
            _permissions = overview.FirstOrDefault(entry => entry.AppId == AppId);
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            // A host without the administration services: declared capabilities are still shown.
        }

        if (_app.Kind == AppKind.Service)
        {
            await LoadServiceAsync();
        }
    }

    private async Task LoadServiceAsync()
    {
        try
        {
            HackServiceController[] services = await HackServiceController.GetServicesAsync(Context.CancellationToken);
            _service = services.FirstOrDefault(service => service.ServiceName == AppId);
            _serviceMessage = _service is null ? L["Details.ServiceUnavailable"].Value : null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            _serviceMessage = L["Details.ServiceError", exception.Message];
        }
    }

    private Task StartServiceAsync() => RunServiceAsync(service => service.StartAsync(Context.CancellationToken), L["Details.ServiceStarted"]);

    private Task StopServiceAsync() => RunServiceAsync(service => service.StopAsync(Context.CancellationToken), L["Details.ServiceStopped"]);

    private Task ChangeStartModeAsync(ChangeEventArgs e) =>
        Enum.TryParse(e.Value?.ToString(), out ServiceStartMode mode)
            ? RunServiceAsync(service => service.SetStartTypeAsync(mode, Context.CancellationToken), L["Details.StartModeSet", StartModeLabel(mode)])
            : Task.CompletedTask;

    private async Task RunServiceAsync(Func<HackServiceController, Task> action, string success)
    {
        if (_service is null || _isBusy)
        {
            return;
        }

        _isBusy = true;
        try
        {
            await action(_service);
            _serviceMessage = success;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidOperationException)
        {
            _serviceMessage = exception.Message;
        }
        finally
        {
            _isBusy = false;
        }
    }

    private string KindLabel(AppKind kind) => kind switch
    {
        AppKind.Window => L["Details.Kind.Window"],
        AppKind.Terminal => L["Details.Kind.Terminal"],
        AppKind.Service => L["Details.Kind.Service"],
        AppKind.Web => L["Details.Kind.Web"],
        _ => kind.ToString()
    };

    private string StateLabel(HackPermissionState state) => state switch
    {
        HackPermissionState.System => L["Details.Permission.System"],
        HackPermissionState.Allowed => L["Details.Permission.Allowed"],
        HackPermissionState.Pending => L["Details.Permission.Pending"],
        HackPermissionState.DeniedByUser => L["Details.Permission.Refused", L["Permissions.State.DeniedByUser"].Value],
        HackPermissionState.DeniedByAdministrator => L["Details.Permission.Refused", L["Permissions.State.DeniedByAdministrator"].Value],
        HackPermissionState.BlockedByPolicy => L["Details.Permission.Refused", L["Permissions.State.BlockedByPolicy"].Value],
        _ => L["Details.Permission.Refused", state.ToString()]
    };

    /// <summary>A service start mode as shown in the list (the option's value stays the enum name).</summary>
    private string StartModeLabel(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => L["StartMode.Automatic"],
        ServiceStartMode.Manual => L["StartMode.Manual"],
        ServiceStartMode.Disabled => L["StartMode.Disabled"],
        _ => mode.ToString()
    };

    /// <summary>A service controller status, translated.</summary>
    private string ServiceStatusLabel(HackServiceControllerStatus status) => status switch
    {
        HackServiceControllerStatus.Running => L["Details.ServiceStatus.Running"],
        HackServiceControllerStatus.Stopped => L["Details.ServiceStatus.Stopped"],
        HackServiceControllerStatus.StartPending => L["Details.ServiceStatus.StartPending"],
        HackServiceControllerStatus.StopPending => L["Details.ServiceStatus.StopPending"],
        _ => status.ToString()
    };

    /// <summary>A handler as a reader sees it: <c>x-scheme-handler/https</c> becomes "https: links".</summary>
    private string DescribeHandler(HackAppFileHandler handler)
    {
        if (handler.MediaType is { } mediaType
            && mediaType.StartsWith(AppIntentIds.SchemeHandlerMediaTypePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return L["Details.Links", mediaType[AppIntentIds.SchemeHandlerMediaTypePrefix.Length..]];
        }

        string extensions = string.Join(" ", handler.Extensions);
        return handler.MediaType is null ? extensions
            : extensions.Length == 0 ? handler.MediaType
            : $"{handler.MediaType} ({extensions})";
    }
}
