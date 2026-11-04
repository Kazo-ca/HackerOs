using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Unzip;

/// <summary>
/// Manifest of "unzip" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class UnzipApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.unzip";

    /// <inheritdoc />
    public override string Name => "unzip";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(UnzipCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "List, test and extract files from a zip archive";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("unzip", "unzip [-l] [-t] [-o] [-q] [-p] [-d dir] archive.zip [file...]");

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
