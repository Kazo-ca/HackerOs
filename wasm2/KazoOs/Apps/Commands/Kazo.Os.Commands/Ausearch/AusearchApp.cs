using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Ausearch;

/// <summary>
/// Manifest of "ausearch" (ADR 0100 section 3). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class AusearchApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.ausearch";

    /// <inheritdoc />
    public override string Name => "ausearch";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(AusearchCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Search the filesystem audit log";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemUserHomeRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("ausearch", "ausearch [-f path] [-ua user] [-c comm] [--success yes|no] [-if file]");

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
