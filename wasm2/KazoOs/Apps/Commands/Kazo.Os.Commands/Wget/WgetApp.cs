using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Wget;

/// <summary>
/// Manifest of "wget" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class WgetApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.wget";

    /// <inheritdoc />
    public override string Name => "wget";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(WgetCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Download files from the network";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite, AppCapabilities.NetworkRealAccess, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("wget", "wget [-O file] [-P dir] [-q] [-S] [--spider] url...");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.08,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.05,
        BurstNetworkIoWeight: 0.6);
}
