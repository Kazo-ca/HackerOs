using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Platform.Core.Backup;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppSystemBackupGateway"/> (ADR 0099, section 1): checks <c>system.backup</c>,
/// asks the OS authentication agent for an administrator, then lets <see cref="SystemBackupService"/>
/// back up or restore the machine.
/// </summary>
/// <remarks>
/// A restore picks and validates the archive <b>before</b> asking for the administrator: the host's
/// file picker needs the user's recent click (or Enter in a terminal), and a bad file must not cost
/// anyone a password prompt.
/// </remarks>
internal sealed class AppSystemBackupGateway : IAppSystemBackupGateway
{
    private readonly Func<SystemBackupService>? _backup;
    private readonly ICapabilityChecker _capabilities;
    private readonly AdministratorGate _gate;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="backup">Lazily resolved backup service; <see langword="null"/> on a host without one.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    /// <param name="gate">Administrator authentication for this app.</param>
    public AppSystemBackupGateway(Func<SystemBackupService>? backup, ICapabilityChecker capabilities, AdministratorGate gate)
    {
        _backup = backup;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public bool IsSupported => _backup is not null;

    /// <inheritdoc />
    public async ValueTask<HackBackupInfo> CreateAsync(CancellationToken cancellationToken = default)
    {
        SystemBackupService backup = Require();
        string admin = await _gate.AuthenticateAsync(
            _gate.Text["Admin.Backup"],
            SystemBackupService.CreateAction,
            "machine",
            cancellationToken).ConfigureAwait(false);
        return await backup.CreateAsync(admin, _gate.Principal, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<HackBackupInfo?> RestoreAsync(HackBackupRestoreMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown restore mode.");
        }

        SystemBackupService backup = Require();
        SystemBackupCandidate? candidate = await backup.PickAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            return null;
        }

        string what = mode == HackBackupRestoreMode.Replace
            ? _gate.Text["Admin.RestoreReplace", candidate.File.Name]
            : _gate.Text["Admin.RestoreMerge", candidate.File.Name];
        string admin = await _gate.AuthenticateAsync(what, SystemBackupService.RestoreAction, "machine", cancellationToken)
            .ConfigureAwait(false);
        await backup.RestoreAsync(candidate, mode, admin, _gate.Principal).ConfigureAwait(false);
        return candidate.ToInfo();
    }

    private SystemBackupService Require()
    {
        _capabilities.Require(AppCapabilities.SystemBackup);
        return _backup?.Invoke() ?? throw new NotSupportedException("This Kazo OS host cannot back up or restore the machine.");
    }
}
