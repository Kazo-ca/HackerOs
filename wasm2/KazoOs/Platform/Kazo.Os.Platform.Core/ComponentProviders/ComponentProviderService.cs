using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.ComponentProviders;

/// <summary>Gives the component host the provider an administrator chose for a contract (ADR 0156 §2).</summary>
public interface IComponentProviderSelection
{
    /// <summary>
    /// Reads the app chosen in <c>/etc/kazoos/components.config</c> for <paramref name="contract"/>,
    /// or <see langword="null"/> when none is (the File Explorer then applies). The file is read at
    /// each call, so a change made with <c>sudo nano</c> applies at the next control shown.
    /// </summary>
    /// <param name="contract">Contract name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<string?> GetSelectedProviderAsync(string contract, CancellationToken cancellationToken = default);
}

/// <summary>One app that registers a contract an administrator chooses.</summary>
/// <param name="AppId">The app.</param>
/// <param name="Name">Its display name, from the manifest.</param>
/// <param name="IsSystemApp">Whether the host declared it a system app (ADR 0048): choosing it needs no approval.</param>
/// <param name="IsInstalled">Whether it is installed for the session user.</param>
public sealed record ComponentProviderCandidate(string AppId, string Name, bool IsSystemApp, bool IsInstalled);

/// <summary>A contract an administrator chooses, its providers and the choice.</summary>
/// <param name="Contract">The contract.</param>
/// <param name="SelectedAppId">The app of <c>components.config</c>, or <see langword="null"/> when the default applies.</param>
/// <param name="DefaultAppId">The provider used without a choice: the first installed one in catalog order (the File Explorer).</param>
/// <param name="Providers">Every app registering the contract, in catalog order (system apps first, then by id).</param>
public sealed record ComponentProviderOverview(
    string Contract,
    string? SelectedAppId,
    string? DefaultAppId,
    IReadOnlyList<ComponentProviderCandidate> Providers);

/// <summary>How a change of provider ended.</summary>
public enum ComponentProviderChangeOutcome
{
    /// <summary>The choice was written to <c>components.config</c>.</summary>
    Applied,

    /// <summary>The administrator refused in the approval window: nothing was written.</summary>
    Refused,

    /// <summary>The approval window was closed, or no administrator authenticated: nothing was written.</summary>
    Dismissed,

    /// <summary>No approval window can be shown on this host: nothing was written.</summary>
    Unavailable
}

/// <summary>
/// Owns <c>/etc/kazoos/components.config</c> (ADR 0156): which app provides the OS's file view and
/// file dialogs. It lists the candidates for Settings, carries out an administrator's choice and
/// removes the choice of an app uninstalled for the machine.
/// </summary>
/// <remarks>
/// <para>
/// Choosing an app that is not a system app is the dangerous case: its code then runs inside every
/// app that shows files, with that app's rights, like a shell extension loaded into
/// <c>explorer.exe</c>. It therefore goes through the approval window of the dangerous capabilities
/// (ADR 0049) with the risk sentence of the contract, and only a verified administrator's attempt
/// writes the file. Choosing a system app, or going back to the default, only needs an administrator,
/// asked by the caller (the gateway's authentication agent, ADR 0055 §5).
/// </para>
/// <para>
/// Every write is an audited system operation of the OS, as <c>pkexec</c> runs its helper as root,
/// and every decision is written to <c>/var/log/auth.log</c> (<c>component.provider</c>).
/// </para>
/// </remarks>
public sealed class ComponentProviderService : IComponentProviderSelection
{
    /// <summary>The audit action of a choice, in <c>/var/log/auth.log</c>.</summary>
    public const string AuditAction = "component.provider";

    private readonly AppCatalog _catalog;
    private readonly IAppEnablementRegistry _enablement;
    private readonly IOsSettingsService _settings;
    private readonly IAdminElevationVerifier _verifier;
    private readonly IAuditLog _auditLog;
    private readonly Action<string>? _reportInformation;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Creates the service.</summary>
    /// <param name="catalog">Catalog: who registers which contract, and which apps are system apps.</param>
    /// <param name="enablement">Installed state for the session user.</param>
    /// <param name="settings">OS settings files.</param>
    /// <param name="verifier">Administrator credential verifier (ADR 0042), for the approval window.</param>
    /// <param name="auditLog">Audit log writing <c>/var/log/auth.log</c>.</param>
    /// <param name="reportInformation">Writes a line to <c>/var/log/syslog</c> (source <c>components</c>), or <see langword="null"/>.</param>
    /// <param name="clock">Clock for audit timestamps; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public ComponentProviderService(
        AppCatalog catalog,
        IAppEnablementRegistry enablement,
        IOsSettingsService settings,
        IAdminElevationVerifier verifier,
        IAuditLog auditLog,
        Action<string>? reportInformation = null,
        Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _enablement = enablement ?? throw new ArgumentNullException(nameof(enablement));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _reportInformation = reportInformation;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The contracts an administrator chooses, in the order Settings shows them.</summary>
    public static IReadOnlyList<string> Contracts { get; } = [AppComponentContracts.FileView, AppComponentContracts.FileDialogs];

    /// <inheritdoc />
    public async ValueTask<string?> GetSelectedProviderAsync(string contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (!ComponentProvidersDocument.IsChosenByAdministrator(contract))
        {
            return null;
        }

        IReadOnlyDictionary<string, string> choices = await ReadChoicesAsync(cancellationToken).ConfigureAwait(false);
        return choices.TryGetValue(contract, out string? appId) ? appId : null;
    }

    /// <summary>Lists each contract an administrator chooses with its providers and the current choice.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<ComponentProviderOverview>> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string> choices = await ReadChoicesAsync(cancellationToken).ConfigureAwait(false);
        return [.. Contracts.Select(contract =>
        {
            ComponentProviderCandidate[] providers = [.. _catalog.GetComponentProviders(contract)
                .Select(provider => new ComponentProviderCandidate(
                    provider.AppId,
                    _catalog.Manifests[provider.AppId].Name,
                    _catalog.IsSystemApp(provider.AppId),
                    _enablement.IsEnabled(provider.AppId)))];
            return new ComponentProviderOverview(
                contract,
                choices.TryGetValue(contract, out string? selected) ? selected : null,
                providers.FirstOrDefault(provider => provider.IsInstalled)?.AppId,
                providers);
        })];
    }

    /// <summary>
    /// Checks a choice before anyone is asked for a password: every contract is one an administrator
    /// chooses, and the app registers each of them and is installed.
    /// </summary>
    /// <param name="contracts">The contracts to set, at least one.</param>
    /// <param name="appId">The app, or <see langword="null"/> to go back to the default.</param>
    /// <exception cref="ArgumentException">The choice is not possible.</exception>
    public void ValidateChoice(IReadOnlyList<string> contracts, string? appId)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        if (contracts.Count == 0)
        {
            throw new ArgumentException("Name at least one contract.", nameof(contracts));
        }

        foreach (string contract in contracts)
        {
            if (contract is null || !ComponentProvidersDocument.IsChosenByAdministrator(contract))
            {
                throw new ArgumentException($"The provider of '{contract}' is not chosen by an administrator.", nameof(contracts));
            }

            if (appId is not null && !_catalog.GetComponentProviders(contract).Any(provider => provider.AppId == appId))
            {
                throw new ArgumentException($"The app '{appId}' does not provide '{contract}'.", nameof(appId));
            }
        }

        if (appId is not null && !_enablement.IsEnabled(appId))
        {
            throw new ArgumentException($"The app '{appId}' is not installed.", nameof(appId));
        }
    }

    /// <summary>Gets the display name of <paramref name="appId"/>, or the id when the catalog does not know it.</summary>
    /// <param name="appId">The app.</param>
    public string GetAppName(string appId) =>
        _catalog.Manifests.TryGetValue(appId, out AppManifest? manifest) ? manifest.Name : appId;

    /// <summary>Gets whether choosing <paramref name="appId"/> needs the approval window (an app that is not a system app).</summary>
    /// <param name="appId">The app, or <see langword="null"/> for the default.</param>
    public bool RequiresApproval(string? appId) => appId is not null && !_catalog.IsSystemApp(appId);

    /// <summary>
    /// Writes a choice an administrator already authorized (a system app, or back to the default) and
    /// logs it to <c>/var/log/auth.log</c>.
    /// </summary>
    /// <param name="contracts">The contracts to set.</param>
    /// <param name="appId">The app, or <see langword="null"/> to go back to the default.</param>
    /// <param name="administratorLogin">The administrator who authenticated.</param>
    /// <param name="requestedBy">The session user who asked.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task ApplyAsync(
        IReadOnlyList<string> contracts,
        string? appId,
        string administratorLogin,
        AuthenticatedPrincipal requestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);
        ValidateChoice(contracts, appId);
        await WriteAsync(contracts, appId, cancellationToken).ConfigureAwait(false);
        Record(administratorLogin, contracts, appId, requestedBy, AuditOutcome.Success, failure: null);
    }

    /// <summary>
    /// Asks the approval window of the dangerous capabilities (ADR 0049) to let a third-party app
    /// provide <paramref name="contracts"/>, with the risk sentence of each one; a verified
    /// administrator's attempt writes the choice.
    /// </summary>
    /// <param name="contracts">The contracts to set.</param>
    /// <param name="appId">The app, which is not a system app.</param>
    /// <param name="requestedBy">The session user who asked.</param>
    /// <param name="prompt">The approval window, or <see langword="null"/> when the host has none.</param>
    /// <param name="language">The language of the texts, those of the calling process (ADR 0104).</param>
    /// <param name="cancellationToken">Cancels the window.</param>
    /// <returns>How the window ended; only <see cref="ComponentProviderChangeOutcome.Applied"/> wrote anything.</returns>
    public async Task<ComponentProviderChangeOutcome> ApproveAsync(
        IReadOnlyList<string> contracts,
        string appId,
        AuthenticatedPrincipal requestedBy,
        IDangerousCapabilityApprovalPrompt? prompt,
        CultureInfo? language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(requestedBy);
        ValidateChoice(contracts, appId);
        if (prompt is null)
        {
            return ComponentProviderChangeOutcome.Unavailable;
        }

        int failures = 0;
        bool approved = false;
        using SemaphoreSlim attemptGate = new(1, 1);

        async Task<DangerousCapabilityApprovalAttempt> AttemptAsync(string login, string password, CancellationToken ct)
        {
            await attemptGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (approved)
                {
                    return new DangerousCapabilityApprovalAttempt(true, DangerousCapabilityApprovalRequest.MaxAttempts - failures);
                }

                if (failures >= DangerousCapabilityApprovalRequest.MaxAttempts)
                {
                    return new DangerousCapabilityApprovalAttempt(false, 0, "too-many-attempts");
                }

                AdminElevationVerificationResult verification = await _verifier
                    .VerifyAsync(login ?? string.Empty, password ?? string.Empty, ct)
                    .ConfigureAwait(false);
                if (!verification.Success)
                {
                    failures++;
                    Record(login ?? string.Empty, contracts, appId, requestedBy, AuditOutcome.Denied, verification.FailureReason);
                    return new DangerousCapabilityApprovalAttempt(
                        false, DangerousCapabilityApprovalRequest.MaxAttempts - failures, verification.FailureReason);
                }

                await WriteAsync(contracts, appId, ct).ConfigureAwait(false);
                approved = true;
                Record(verification.LoginName ?? login ?? string.Empty, contracts, appId, requestedBy, AuditOutcome.Success, failure: null);
                return new DangerousCapabilityApprovalAttempt(true, DangerousCapabilityApprovalRequest.MaxAttempts - failures);
            }
            finally
            {
                attemptGate.Release();
            }
        }

        DangerousCapabilityApprovalRequest request = new(
            _catalog.Manifests[appId],
            [.. contracts.Select(contract => Describe(contract, language))],
            requestedBy,
            AppAuthorityPolicy.Satisfies(requestedBy.Authority, AppAuthority.Administrator) ? requestedBy.LoginName.Value : null,
            AttemptAsync);

        DangerousCapabilityApprovalOutcome outcome;
        try
        {
            outcome = await prompt.RequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = DangerousCapabilityApprovalOutcome.Dismissed;
        }

        // The window cannot forge an approval: only a verified attempt wrote the file.
        if (approved)
        {
            return ComponentProviderChangeOutcome.Applied;
        }

        if (outcome == DangerousCapabilityApprovalOutcome.Refused)
        {
            Record(requestedBy.LoginName.Value, contracts, appId, requestedBy, AuditOutcome.Denied, "refused");
            return ComponentProviderChangeOutcome.Refused;
        }

        return outcome == DangerousCapabilityApprovalOutcome.Unavailable
            ? ComponentProviderChangeOutcome.Unavailable
            : ComponentProviderChangeOutcome.Dismissed;
    }

    /// <summary>
    /// Removes the choice of every app now uninstalled for the whole machine (ADR 0156 §4): the OS
    /// goes back to the default by itself. An app uninstalled for one user only keeps its line; that
    /// user gets the fallback, the others keep the choice.
    /// </summary>
    /// <param name="isUninstalledForMachine">Whether an app is uninstalled for the machine.</param>
    /// <param name="cancellationToken">Cancels the read and the write.</param>
    /// <returns>The contracts whose line was removed.</returns>
    public async Task<IReadOnlyList<string>> RemoveUninstalledAsync(
        Func<string, bool> isUninstalledForMachine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isUninstalledForMachine);
        IReadOnlyDictionary<string, string> choices = await ReadChoicesAsync(cancellationToken).ConfigureAwait(false);
        string[] removed = [.. Contracts.Where(contract => choices.TryGetValue(contract, out string? appId)
            && _catalog.Manifests.ContainsKey(appId)
            && isUninstalledForMachine(appId))];
        if (removed.Length == 0)
        {
            return [];
        }

        await WriteAsync(removed, appId: null, cancellationToken).ConfigureAwait(false);
        foreach (string contract in removed)
        {
            _reportInformation?.Invoke(
                $"component.provider.removed: '{contract}': '{choices[contract]}' was uninstalled; {ComponentProvidersDocument.SystemPath} goes back to the default");
        }

        return removed;
    }

    /// <summary>Describes one contract in the approval window, with its risk sentence (ADR 0156 §3).</summary>
    /// <param name="contract">The contract.</param>
    /// <param name="language">The reader's language; English when <see langword="null"/>.</param>
    /// <remarks>
    /// The texts live in the core's resources under <c>Capability.&lt;contract&gt;.Title</c> and
    /// <c>.Risk</c>, so the window's own translation (<see cref="CapabilityText.Localize"/>) finds them.
    /// </remarks>
    public static CapabilityDescription Describe(string contract, CultureInfo? language)
    {
        ArgumentNullException.ThrowIfNull(contract);
        CapabilityDescription description = new(
            contract,
            $"Provide the OS component '{contract}'",
            CapabilityRisk.Dangerous,
            "This app's code will run inside every app that shows files, with that app's rights.");
        return CapabilityText.Localize(description, language);
    }

    private async ValueTask<IReadOnlyDictionary<string, string>> ReadChoicesAsync(CancellationToken cancellationToken)
    {
        string? content = await _settings.ReadFileAsync(ComponentProvidersDocument.File, cancellationToken).ConfigureAwait(false);
        return ComponentProvidersDocument.Parse(content);
    }

    /// <summary>One read-edit-write of the file at a time, as an audited system operation (<c>writer: null</c>).</summary>
    private async Task WriteAsync(IReadOnlyList<string> contracts, string? appId, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? content = await _settings.ReadFileAsync(ComponentProvidersDocument.File, cancellationToken).ConfigureAwait(false);
            string? edited = content;
            foreach (string contract in contracts)
            {
                edited = ComponentProvidersDocument.Edit(edited, contract, appId);
            }

            // Removing a choice from a file that does not exist leaves nothing to write.
            if (edited is not null && !string.Equals(edited, content, StringComparison.Ordinal))
            {
                await _settings.WriteFileAsync(ComponentProvidersDocument.File, edited, writer: null, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Record(
        string actor,
        IReadOnlyList<string> contracts,
        string? appId,
        AuthenticatedPrincipal requestedBy,
        AuditOutcome outcome,
        string? failure)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal)
        {
            ["contracts"] = string.Join(",", contracts),
            ["provider"] = appId ?? "default",
            ["user"] = requestedBy.LoginName.Value
        };
        if (failure is not null)
        {
            properties["reason"] = failure;
        }

        _auditLog.Record(new AuditEntry(_clock(), Guid.NewGuid(), actor, AuditAction, ComponentProvidersDocument.SystemPath, outcome, properties));
    }
}
