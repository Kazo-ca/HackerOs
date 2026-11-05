using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Settings;

/// <summary>
/// Settings &gt; OS components (ADR 0156 §3): for the file view and the file dialogs, the apps that
/// provide them, the one in use, and the administrator's choice in <c>/etc/kazoos/components.config</c>.
/// </summary>
/// <remarks>
/// <para>
/// Uses only <see cref="KazoComponentProviders"/> (capability <c>apps.manage</c>). Choosing an app that
/// is not a system app makes the OS show its approval window of the dangerous capabilities, with the
/// risk sentence of each contract; a system app, or the way back to the File Explorer, only needs an
/// administrator in the OS's secure window. This panel never sees a password.
/// </para>
/// <para>
/// The two contracts are chosen separately, but when the chosen app provides both the panel offers to
/// set the other one in the same change (one approval), checked by default.
/// </para>
/// </remarks>
public sealed partial class OsComponentsPanel
{
    private IReadOnlyList<KazoComponentContractInfo> _overview = [];
    private PendingChoice? _pending;
    private bool _applyToOther = true;
    private bool _isAvailable = true;
    private bool _isBusy;
    private string? _message;
    private bool _messageIsError;
    private IStringLocalizer<Strings>? _localizer;

    private IAppExecutionContext Context => AppContext
        ?? throw new InvalidOperationException("This panel must be rendered inside the Settings window.");

    /// <summary>The panel's texts in the language of the Settings process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>Gets the contracts and their providers, as last read.</summary>
    internal IReadOnlyList<KazoComponentContractInfo> Overview => _overview;

    /// <summary>Gets the choice waiting for confirmation, or <see langword="null"/>.</summary>
    internal PendingChoice? Pending => _pending;

    /// <summary>Gets the text of the last outcome, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is NotSupportedException or UnauthorizedAccessException)
        {
            // A host without the service, or a Settings build without apps.manage.
            _isAvailable = false;
        }
    }

    private async Task RefreshAsync() =>
        _overview = await KazoComponentProviders.GetOverviewAsync(Context, Context.CancellationToken);

    /// <summary>Prepares the choice of <paramref name="provider"/>; nothing is written before <see cref="ApplyAsync"/>.</summary>
    internal void Choose(KazoComponentContractInfo contract, KazoComponentProviderInfo provider)
    {
        _message = null;
        bool unchanged = contract.SelectedAppId is { } selected
            ? selected == provider.AppId
            : provider.AppId == contract.DefaultAppId;
        if (unchanged)
        {
            _pending = null;
            return;
        }

        // Choosing the default system app is the way back: the line is removed, the file stays minimal.
        _pending = provider.IsSystemApp && provider.AppId == contract.DefaultAppId
            ? CreatePending(contract, null, null, needsApproval: false)
            : CreatePending(contract, provider.AppId, provider.Name, needsApproval: !provider.IsSystemApp);
    }

    /// <summary>Prepares the way back to the default provider (the File Explorer).</summary>
    internal void ChooseDefault(KazoComponentContractInfo contract)
    {
        _message = null;
        _pending = CreatePending(contract, null, null, needsApproval: false);
    }

    private PendingChoice CreatePending(KazoComponentContractInfo contract, string? appId, string? appName, bool needsApproval)
    {
        // Offer the other contract when the same choice makes sense there: the app provides it too,
        // or, for the way back, the other contract has a choice of its own to undo.
        KazoComponentContractInfo? other = _overview.FirstOrDefault(item => item.Contract != contract.Contract);
        bool offerOther = other is not null && (appId is null
            ? other.SelectedAppId is not null
            : other.Providers.Any(provider => provider.AppId == appId && provider.IsInstalled) && other.SelectedAppId != appId);
        _applyToOther = true;
        return new PendingChoice(contract.Contract, appId, appName ?? string.Empty, offerOther ? other!.Contract : null, needsApproval);
    }

    /// <summary>Cancels the choice waiting for confirmation.</summary>
    internal void Cancel() => _pending = null;

    /// <summary>Carries out the pending choice; the OS asks for the approval or the administrator.</summary>
    internal async Task ApplyAsync()
    {
        if (_pending is not { } pending || _isBusy)
        {
            return;
        }

        IReadOnlyList<string> contracts = pending.OtherContract is { } other && _applyToOther
            ? [pending.Contract, other]
            : [pending.Contract];
        _isBusy = true;
        _message = null;
        try
        {
            await KazoComponentProviders.SetProviderAsync(Context, contracts, pending.AppId, Context.CancellationToken);
            _pending = null;
            Show(pending.AppId is null ? L["Components.Saved.Default"] : L["Components.Saved", pending.AppName], isError: false);
            await RefreshAsync();
        }
        catch (UnauthorizedAccessException)
        {
            Show(L["Permissions.Error.NotConfirmed"], isError: true);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Show(L["Components.Error.CannotSave", exception.Message], isError: true);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void Show(string message, bool isError)
    {
        _message = message;
        _messageIsError = isError;
    }

    /// <summary>The provider in use: the administrator's choice when installed, else the default.</summary>
    private static bool IsActive(KazoComponentContractInfo contract, KazoComponentProviderInfo provider)
    {
        bool selectedInstalled = contract.SelectedAppId is { } selected
            && contract.Providers.Any(item => item.AppId == selected && item.IsInstalled);
        return selectedInstalled ? provider.AppId == contract.SelectedAppId : provider.AppId == contract.DefaultAppId;
    }

    private string ProviderClass(KazoComponentContractInfo contract, KazoComponentProviderInfo provider)
    {
        string css = "components-provider";
        if (IsActive(contract, provider))
        {
            css += " active";
        }

        if (_pending is { } pending && pending.Contract == contract.Contract && pending.AppId == provider.AppId)
        {
            css += " pending";
        }

        return css;
    }

    private string ContractTitle(string contract) => contract switch
    {
        KazoComponentContracts.FileView => L["Components.FileView.Title"],
        KazoComponentContracts.FileDialogs => L["Components.FileDialogs.Title"],
        _ => contract
    };

    private string ContractDescription(string contract) => contract switch
    {
        KazoComponentContracts.FileView => L["Components.FileView.Description"],
        KazoComponentContracts.FileDialogs => L["Components.FileDialogs.Description"],
        _ => string.Empty
    };

    /// <summary>A choice shown for confirmation before the OS is asked.</summary>
    /// <param name="Contract">The contract clicked.</param>
    /// <param name="AppId">The app, or <see langword="null"/> for the default.</param>
    /// <param name="AppName">The app's name, for the texts.</param>
    /// <param name="OtherContract">The other contract the same change can set, or <see langword="null"/>.</param>
    /// <param name="NeedsApproval">Whether the OS will show its approval window (a third-party app).</param>
    internal sealed record PendingChoice(string Contract, string? AppId, string AppName, string? OtherContract, bool NeedsApproval);
}
