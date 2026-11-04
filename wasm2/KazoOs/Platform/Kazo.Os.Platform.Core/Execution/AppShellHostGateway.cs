using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Shell;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Trusted <see cref="IAppShellHostGateway"/> (ADR 0056, section 7): checks <c>shell.host</c>,
/// then asks <see cref="ShellHostService"/> to start the shell as a child of the app's process.
/// </summary>
internal sealed class AppShellHostGateway(
    Func<ShellHostService> serviceProvider,
    ICapabilityChecker capabilities,
    AuthenticatedPrincipal principal,
    Func<IAppExecutionContext> caller,
    Func<ProcessRecord, IAppExecutionContext> createShellContext) : IAppShellHostGateway
{
    /// <inheritdoc />
    public Task<IHackShell> StartAsync(KazoShellOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        capabilities.Require(AppCapabilities.ShellHost);
        return serviceProvider().StartAsync(caller(), principal, createShellContext, options, cancellationToken);
    }
}
