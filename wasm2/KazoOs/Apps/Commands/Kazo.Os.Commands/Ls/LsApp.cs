using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Ls;

/// <summary>
/// Manifest of "ls" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class LsApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.cmd.ls";

    /// <inheritdoc />
    public override string Name => "ls";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(LsCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "List directory contents";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("ls", "ls [-a] [-l] [path ...]", ["dir"]);

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.02,
        BurstMemoryWeight: 0.1,
        BaselineStorageIoWeight: 0.05,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
