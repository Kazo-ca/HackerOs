using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Netstat;

/// <summary>
/// Manifest of "netstat" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class NetstatApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.netstat";

    /// <inheritdoc />
    public override string Name => "netstat";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(NetstatCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Print network connections, routing tables and interface statistics";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite, AppCapabilities.FileSystemSystemRead, AppCapabilities.ProcessList];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("netstat", "netstat [-tulnap] [-i] [-r] [-s]", ["ss"]);

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.01,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.02,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
