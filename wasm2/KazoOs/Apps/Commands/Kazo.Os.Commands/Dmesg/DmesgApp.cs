using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Dmesg;

/// <summary>
/// Manifest of "dmesg" (ADR 0126, section 3). The build generates <c>manifests/&lt;appId&gt;.json</c>
/// from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class DmesgApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.dmesg";

    /// <inheritdoc />
    public override string Name => "dmesg";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(DmesgCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Print the kernel ring buffer";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    // filesystem.system.read: /dev/kmsg and /proc/uptime are system files, as for ps.
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("dmesg", "dmesg [-T] [-x] [-l LEVEL[,LEVEL...]]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.02,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
