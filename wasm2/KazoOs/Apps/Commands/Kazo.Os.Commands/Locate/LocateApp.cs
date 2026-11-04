using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Locate;

/// <summary>
/// Manifest of "locate" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class LocateApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.locate";

    /// <inheritdoc />
    public override string Name => "locate";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(LocateCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Find files by name anywhere in the filesystem";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("locate", "locate [-i] [-b] [-c] [-l count] pattern...");

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
