using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Head;

/// <summary>
/// Manifest of "head" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class HeadApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.head";

    /// <inheritdoc />
    public override string Name => "head";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(HeadCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Output the first part of files";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("head", "head [-n [-]count | -count] [-c bytes] [-qv] [file...]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.05,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
