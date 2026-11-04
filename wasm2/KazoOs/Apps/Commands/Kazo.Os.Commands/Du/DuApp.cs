using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Du;

/// <summary>
/// Manifest of "du" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class DuApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.du";

    /// <inheritdoc />
    public override string Name => "du";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(DuCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Estimate file space usage";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("du", "du [-a] [-s] [-h] [-c] [-d depth] [path...]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.08,
        BaselineStorageIoWeight: 0.05,
        BurstStorageIoWeight: 0.5,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
