using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Cat;

/// <summary>
/// Manifest of "cat" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class CatApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.cmd.cat";

    /// <inheritdoc />
    public override string Name => "cat";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CatCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Concatenate files and print on the standard output";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemSystemRead, AppCapabilities.NetworkSimulatedRead, AppCapabilities.NetworkRealAccess];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("cat", "cat [file ...]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.02,
        BurstMemoryWeight: 0.1,
        BaselineStorageIoWeight: 0.05,
        BurstStorageIoWeight: 0.3,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
