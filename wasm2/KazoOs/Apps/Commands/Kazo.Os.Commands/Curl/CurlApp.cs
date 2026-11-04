using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Curl;

/// <summary>
/// Manifest of "curl" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class CurlApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.curl";

    /// <inheritdoc />
    public override string Name => "curl";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CurlCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "Simulated URL data transfer — fetches content from simulated websites";

    /// <inheritdoc />
    public override string Category => "network";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkSimulatedWrite, AppCapabilities.NetworkRealAccess,
         AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal =>
        new("curl", "curl [-I] [-v] [-L] [-s] [-f] [-o file|-O] [-H header] [-X <method>] [-d <data>] <url>");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.08,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
