using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Administration;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Power;
using HackerOs.Simulation.Abstractions.Gateways;

namespace HackerOs.Platform.Core.Administration;

/// <summary>
/// Trusted <see cref="IAppSystemPowerGateway"/> (ADR 0056, section 11): checks <c>system.power</c>,
/// asks the OS authentication agent for an administrator, then lets <see cref="SystemPowerService"/>
/// restart or reset the machine.
/// </summary>
internal sealed class AppSystemPowerGateway : IAppSystemPowerGateway
{
    private readonly Func<SystemPowerService>? _power;
    private readonly ICapabilityChecker _capabilities;
    private readonly AdministratorGate _gate;

    /// <summary>Creates the gateway for one app instance.</summary>
    /// <param name="power">Lazily resolved power service; <see langword="null"/> on a host that cannot restart.</param>
    /// <param name="capabilities">The app's capability checker.</param>
    /// <param name="gate">Administrator authentication for this app.</param>
    public AppSystemPowerGateway(Func<SystemPowerService>? power, ICapabilityChecker capabilities, AdministratorGate gate)
    {
        _power = power;
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <inheritdoc />
    public async ValueTask RebootAsync(CancellationToken cancellationToken = default)
    {
        SystemPowerService power = Require();
        string admin = await _gate.AuthenticateAsync(_gate.Text["Admin.Reboot"], "system.reboot", "machine", cancellationToken).ConfigureAwait(false);
        await power.RebootAsync(admin, _gate.Principal).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask FactoryResetAsync(CancellationToken cancellationToken = default)
    {
        SystemPowerService power = Require();
        string admin = await _gate.AuthenticateAsync(
            _gate.Text["Admin.FactoryReset"],
            "system.factory-reset",
            "machine",
            cancellationToken).ConfigureAwait(false);
        await power.FactoryResetAsync(admin, _gate.Principal).ConfigureAwait(false);
    }

    private SystemPowerService Require()
    {
        _capabilities.Require(AppCapabilities.SystemPower);
        return _power?.Invoke() ?? throw new NotSupportedException("This KazoOS host cannot restart the machine.");
    }
}
