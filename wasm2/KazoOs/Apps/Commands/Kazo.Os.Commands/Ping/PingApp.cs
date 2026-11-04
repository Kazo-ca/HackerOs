using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Ping;

/// <summary>
/// Manifest of "ping" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class PingApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.ping";

    /// <inheritdoc />
    public override string Name => "ping";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(PingCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description =>
        "Simulated network ping command — tests reachability of a simulated host";

    /// <inheritdoc />
    public override string Category => "network";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkRealAccess];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("ping", "ping <hostname|ip>");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
