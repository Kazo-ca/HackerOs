using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Installed Apps (ADR 0048 section 5): one row per catalog app with its install state,
/// an Uninstall button with a confirmation listing the dependents and the processes it will stop
/// (ADR 0097, section 5), and Reinstall where the user may
/// change the scope that uninstalled it. System apps get a badge and no button.
/// </summary>
/// <remarks>
/// Uses only the public SDK (ADR 0055, ADR 0056 section 3): <see cref="IAppManagementGateway"/>
/// (capability <c>apps.manage</c>) for the list and the changes, <see cref="IAppCatalogGateway.Changed"/>
/// to refresh after any <c>apps.config</c> change, and <see cref="HackUser"/> to know whether the
/// "for all users" scope is offered. A machine-scope change makes the OS ask an administrator to
/// authenticate in its own window; this panel never sees a password.
/// </remarks>
public partial class InstalledAppsPanel : IDisposable
{
    private IReadOnlyList<HackAppInstallInfo>? _apps;
    private HackAppInstallInfo? _pending;
    private HackUninstallPlan? _plan;
    private HackInstallScope _scope = HackInstallScope.User;
    private IAppCatalogGateway? _catalog;
    private bool _isBusy;
    private string? _message;

    /// <summary>
    /// Gets the Settings window's execution context, cascaded by the window host. The panel derives
    /// from <c>HackComponentBase</c> so its lifecycle and event handlers run inside the app's ambient
    /// frame (ADR 0067: otherwise their time would be charged to the compositor).
    /// </summary>
    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>
    /// Gets or sets the one app to show, on its page in Settings (ADR 0096, section 4): only its row,
    /// whatever its kind, without the tab's introduction. <see langword="null"/> lists every app.
    /// </summary>
    [Parameter]
    public string? OnlyAppId { get; set; }

    /// <summary>Window apps, sorted by name. Services are managed from System Monitor's Services tab.</summary>
    private IReadOnlyList<HackAppInstallInfo> Applications => OnlyAppId is { } only
        ? [.. (_apps ?? []).Where(entry => entry.App.Id == only)]
        : [.. (_apps ?? []).Where(static entry => entry.App.Kind == AppKind.Window)];

    /// <summary>Terminal commands, sorted by name; all are system apps on the Ecosystem host.</summary>
    private IReadOnlyList<HackAppInstallInfo> Commands => OnlyAppId is not null
        ? []
        : [.. (_apps ?? []).Where(static entry => entry.App.Kind == AppKind.Terminal)];

    /// <summary>Only an administrator is offered "for all users" (ADR 0048 section 3); the OS still asks for their password.</summary>
    private bool IsAdministrator => HackUser.Describe(Context).IsAdministrator;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        _catalog = Context.Catalog;
        _catalog.Changed += OnCatalogChanged;
        await ReloadAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_catalog is not null)
        {
            _catalog.Changed -= OnCatalogChanged;
        }
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => _ = InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    private async Task ReloadAsync()
    {
        try
        {
            _apps = await Context.AppManagement.GetAllAsync(Context.CancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            _apps = [];
            _message = L["Apps.Error.NotAllowed"];
        }
    }

    private string NameOf(string appId) =>
        _apps?.FirstOrDefault(entry => entry.App.Id == appId)?.App.Name ?? appId;

    /// <summary>The web sites of an uninstall closure: their browser database is deleted with them (ADR 0088 §4).</summary>
    private IEnumerable<string> WebSitesIn(IReadOnlyList<string> closure) =>
        closure.Where(id => _apps?.FirstOrDefault(entry => entry.App.Id == id)?.App.Kind == Kazo.Os.App.Abstractions.AppKind.Web);

    /// <summary>The result line after an uninstall: which processes were stopped, and which had to be forced.</summary>
    private string? DescribeStopped(HackUninstallReport report)
    {
        if (report.StoppedProcesses.Count == 0)
        {
            return null;
        }

        string stopped = string.Join(", ", report.StoppedProcesses.Select(p => L["Apps.ProcessItem", p.Name, p.Pid].Value));
        IReadOnlyList<HackRunningProcessInfo> forced = report.ForcedProcesses;
        return forced.Count == 0
            ? L["Apps.Stopped", stopped]
            : L["Apps.StoppedAndForced", stopped, string.Join(", ", forced.Select(p => L["Apps.ProcessItem", p.Name, p.Pid].Value))];
    }

    private static string RowClass(HackAppInstallInfo entry) =>
        entry.State == HackAppInstallState.Incompatible ? "is-incompatible"
        : entry.State == HackAppInstallState.Damaged ? "is-damaged"
        : entry.App.IsSystemApp ? "is-system"
        : entry.State == HackAppInstallState.Installed ? "is-installed"
        : "is-uninstalled";

    private string ScopeClass(HackInstallScope scope) =>
        scope == _scope ? "scope-option is-selected" : "scope-option";

    private async Task AskUninstall(HackAppInstallInfo entry)
    {
        _message = null;
        _pending = entry;
        _plan = null;
        _scope = HackInstallScope.User;
        try
        {
            // ADR 0097 section 5: the dialog names the processes the uninstall will stop.
            _plan = await Context.AppManagement.PlanUninstallAsync(entry.App.Id, Context.CancellationToken);
        }
        catch (Exception exception) when (exception is NotSupportedException or AppGatewayAccessDeniedException or UnauthorizedAccessException)
        {
            _plan = null;
        }
    }

    private void CancelUninstall()
    {
        if (!_isBusy)
        {
            _pending = null;
            _plan = null;
        }
    }

    private async Task ConfirmUninstallAsync()
    {
        if (_pending is not { } entry)
        {
            return;
        }

        HackInstallScope scope = IsAdministrator ? _scope : HackInstallScope.User;
        HackUninstallReport? report = null;
        bool done = await RunAsync(async () =>
        {
            report = await Context.AppManagement.UninstallWithReportAsync(entry.App.Id, scope, Context.CancellationToken);
            return report.AppIds;
        });
        if (done)
        {
            _pending = null;
            _plan = null;
            _message = report is null ? null : DescribeStopped(report);
        }
    }

    private Task ReinstallAsync(HackAppInstallInfo entry)
    {
        _message = null;
        HackInstallScope scope = entry.State == HackAppInstallState.UninstalledForMachine
            ? HackInstallScope.Machine
            : HackInstallScope.User;
        return RunAsync(() => Context.AppManagement.ReinstallAsync(entry.App.Id, scope, Context.CancellationToken));
    }

    /// <summary>Runs one install change and turns its outcome into a message; returns whether it succeeded.</summary>
    private async Task<bool> RunAsync(Func<ValueTask<IReadOnlyList<string>>> change)
    {
        if (_isBusy)
        {
            return false;
        }

        _isBusy = true;
        _message = null;
        StateHasChanged();
        try
        {
            await change();
            await ReloadAsync();
            return true;
        }
        catch (AppGatewayAccessDeniedException)
        {
            _message = L["Apps.Error.NotAllowed"];
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            _message = L["Apps.Error.NoAdmin"];
            return false;
        }
        catch (InvalidOperationException exception)
        {
            _message = exception.Message;
            return false;
        }
        catch (IOException exception)
        {
            _message = L["Apps.Error.CannotWrite", exception.Message];
            return false;
        }
        finally
        {
            _isBusy = false;
            StateHasChanged();
        }
    }
}
