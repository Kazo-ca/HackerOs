using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Nmap;

/// <summary>
/// Manifest of "nmap" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class NmapApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.nmap";

    /// <inheritdoc />
    public override string Name => "nmap";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(NmapCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Simulated network mapper — scans ports on simulated hosts";

    /// <inheritdoc />
    public override string Category => "network";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkRealAccess];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("nmap", "nmap [-p <range>] [-sV] [-O] <target>");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.15,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
