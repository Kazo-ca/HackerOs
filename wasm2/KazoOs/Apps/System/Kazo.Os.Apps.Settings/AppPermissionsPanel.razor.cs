using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; Applications &gt; Permissions (ADR 0049 section 7): the dangerous-capability policy
/// and, for every app declaring a dangerous capability, each capability's state (system, allowed,
/// refused by you, refused by the administrator, blocked by the policy, pending).
/// </summary>
/// <remarks>
/// Uses only <see cref="HackPermissionAdministration"/> (ADR 0056 section 9, capability
/// <c>permissions.manage</c>). A machine change makes the OS ask an administrator for their password
/// in its own secure window (ADR 0055 section 5); this panel never sees it. Withdrawing one's own
/// refusal needs no administrator. Every change applies at the app's next launch.
/// </remarks>
public sealed partial class AppPermissionsPanel
{
    private IReadOnlyList<HackAppPermissionInfo> _overview = [];
    private HackDangerousCapabilityPolicy _policy;
    private bool _isAvailable = true;
    private string? _message;
    private bool _isBusy;

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

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            // A host without the administration services, or a Settings build without the capability.
            _isAvailable = false;
        }
    }

    private async Task RefreshAsync()
    {
        _policy = await HackPermissionAdministration.GetPolicyAsync(Context, Context.CancellationToken);
        _overview = await HackPermissionAdministration.GetOverviewAsync(Context, Context.CancellationToken);
    }

    private Task ChangePolicyAsync(HackDangerousCapabilityPolicy policy) =>
        policy == _policy
            ? Task.CompletedTask
            : RunAsync(
                () => HackPermissionAdministration.SetPolicyAsync(Context, policy, Context.CancellationToken),
                policy == HackDangerousCapabilityPolicy.Block ? L["Permissions.Message.PolicyBlock"] : L["Permissions.Message.PolicyPrompt"]);

    private Task SetMachineDecisionAsync(
        HackAppPermissionInfo app, HackCapabilityPermissionInfo capability, HackPermissionDecision decision) =>
        RunAsync(
            () => HackPermissionAdministration.SetMachineDecisionAsync(
                Context, app.AppId, capability.Capability, decision, Context.CancellationToken),
            L["Permissions.Message.DecisionSaved", app.AppName]);

    private Task RemoveOwnDenialAsync(HackAppPermissionInfo app, HackCapabilityPermissionInfo capability) =>
        RunAsync(
            () => HackPermissionAdministration.RemoveOwnDenialAsync(
                Context, app.AppId, capability.Capability, Context.CancellationToken),
            L["Permissions.Message.DenialRemoved", app.AppName]);

    private async Task RunAsync(Func<Task> action, string successMessage)
    {
        _isBusy = true;
        _message = null;
        try
        {
            await action();
            _message = successMessage;
            await RefreshAsync();
        }
        catch (UnauthorizedAccessException)
        {
            _message = L["Permissions.Error.NotConfirmed"];
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            _message = L["Permissions.Error.CannotSave", exception.Message];
        }
        finally
        {
            _isBusy = false;
        }
    }

    private string PolicyClass(HackDangerousCapabilityPolicy policy) =>
        policy == _policy ? "policy-option selected" : "policy-option";

    private string StateLabel(HackPermissionState state) => state switch
    {
        HackPermissionState.System => L["Permissions.State.System"],
        HackPermissionState.Allowed => L["Permissions.State.Allowed"],
        HackPermissionState.DeniedByUser => L["Permissions.State.DeniedByUser"],
        HackPermissionState.DeniedByAdministrator => L["Permissions.State.DeniedByAdministrator"],
        HackPermissionState.BlockedByPolicy => L["Permissions.State.BlockedByPolicy"],
        _ => L["Permissions.State.Pending"]
    };

    private static string StateClass(HackPermissionState state) => state switch
    {
        HackPermissionState.System or HackPermissionState.Allowed => "capability-state granted",
        HackPermissionState.Pending => "capability-state pending",
        _ => "capability-state denied"
    };
}
