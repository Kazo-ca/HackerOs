using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.ComponentProviders;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppComponentProviderAdministrationGateway"/> (ADR 0156 §3) over
/// <see cref="ComponentProviderService"/>.
/// </summary>
/// <remarks>
/// Every call requires <c>apps.manage</c>. A choice is validated first (an app that does not provide
/// the contract is refused before any password is asked). A third-party app then goes through the
/// approval window of the dangerous capabilities, whose verified attempt writes the file; a system app
/// or the default only asks the OS authentication agent for an administrator, then the OS writes the
/// file itself. The app never sees the password and its own rights are never widened.
/// </remarks>
internal sealed class AppComponentProviderAdministrationGateway : IAppComponentProviderAdministrationGateway
{
    private readonly Func<ComponentProviderService>? _service;
    private readonly Func<IDangerousCapabilityApprovalPrompt>? _prompt;
    private readonly ICapabilityChecker _capabilities;
    private readonly AdministratorGate _gate;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="service">Lazily resolved service; <see langword="null"/> makes every call unsupported.</param>
    /// <param name="prompt">The approval window of ADR 0049, lazily resolved; <see langword="null"/> refuses third-party providers.</param>
    /// <param name="capabilities">The calling instance's capabilities.</param>
    /// <param name="gate">Administrator authentication for the calling instance.</param>
    public AppComponentProviderAdministrationGateway(
        Func<ComponentProviderService>? service,
        Func<IDangerousCapabilityApprovalPrompt>? prompt,
        ICapabilityChecker capabilities,
        AdministratorGate gate)
    {
        _service = service;
        _prompt = prompt;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<KazoComponentContractInfo>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        IReadOnlyList<ComponentProviderOverview> overview = await Service().GetOverviewAsync(cancellationToken).ConfigureAwait(false);
        return [.. overview.Select(contract => new KazoComponentContractInfo(
            contract.Contract,
            contract.SelectedAppId,
            contract.DefaultAppId,
            [.. contract.Providers.Select(provider => new KazoComponentProviderInfo(
                provider.AppId, provider.Name, provider.IsSystemApp, provider.IsInstalled))]))];
    }

    /// <inheritdoc />
    public async ValueTask SetProviderAsync(IReadOnlyList<string> contracts, string? appId, CancellationToken cancellationToken = default)
    {
        _capabilities.Require(AppCapabilities.AppsManage);
        ArgumentNullException.ThrowIfNull(contracts);
        ComponentProviderService service = Service();
        service.ValidateChoice(contracts, appId);
        string contractList = string.Join(", ", contracts);

        if (service.RequiresApproval(appId))
        {
            ComponentProviderChangeOutcome outcome = await service
                .ApproveAsync(contracts, appId!, _gate.Principal, _prompt?.Invoke(), _gate.Language, cancellationToken)
                .ConfigureAwait(false);
            if (outcome != ComponentProviderChangeOutcome.Applied)
            {
                throw new KazoUnauthorizedAccessException(
                    $"The provider of {contractList} was not changed ({outcome}).", path: ComponentProvidersDocument.SystemPath);
            }

            return;
        }

        string operation = appId is null
            ? _gate.Text["Admin.ComponentProvider.Default", contractList]
            : _gate.Text["Admin.ComponentProvider.Set", service.GetAppName(appId), appId, contractList];
        string admin = await _gate.AuthenticateAsync(operation, ComponentProviderService.AuditAction, ComponentProvidersDocument.SystemPath, cancellationToken)
            .ConfigureAwait(false);
        await service.ApplyAsync(contracts, appId, admin, _gate.Principal, cancellationToken).ConfigureAwait(false);
    }

    private ComponentProviderService Service() =>
        _service?.Invoke() ?? throw new NotSupportedException("This host does not let an administrator choose component providers.");
}
