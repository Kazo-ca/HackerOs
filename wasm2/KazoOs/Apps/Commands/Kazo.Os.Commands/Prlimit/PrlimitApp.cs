using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Prlimit;

/// <summary>
/// Manifest of "prlimit" (ADR 0125, section 4). The build generates <c>manifests/&lt;appId&gt;.json</c>
/// from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class PrlimitApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.prlimit";

    /// <inheritdoc />
    public override string Name => "prlimit";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(PrlimitCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Get and set the resource limits of a process";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    // process.list and filesystem.system.read: reading /proc/<pid>/limits, as ps does; process.manage: changing another process's limits is
    // managing it (ADR 0125, section 4).
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ProcessList, AppCapabilities.ProcessManage, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("prlimit", "prlimit [--pid <pid>] [--cpu[=soft:hard]] [--rss[=]] [--nofile[=]] [--nproc[=]] [--noheadings]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
