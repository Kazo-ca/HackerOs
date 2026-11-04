using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Nslookup;

/// <summary>
/// Manifest of "nslookup" (ADR 0074, section 4). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class NslookupApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.nslookup";

    /// <inheritdoc />
    public override string Name => "nslookup";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(NslookupCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Query the resolver kazodns for a host name or an address, and show which source answered";

    /// <inheritdoc />
    public override string Category => "network";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.NetworkSimulatedRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("nslookup", "nslookup <name|address>");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.01,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.02,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.01);
}
