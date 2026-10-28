using HackerOs.App.Abstractions;

namespace HackerOs.Commands.Mount;

/// <summary>
/// Manifest of "mount" (ADR 0181, section 12). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class MountApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.mount";

    /// <inheritdoc />
    public override string Name => "mount";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(MountCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Mount a folder of the computer as a network drive";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("mount", "mount [-l] | mount -t hostfs [/mnt/name] | mount -o remount /mnt/name");

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
