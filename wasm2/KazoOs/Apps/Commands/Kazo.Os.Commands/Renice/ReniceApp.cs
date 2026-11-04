using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Renice;

/// <summary>
/// Manifest of "renice" (ADR 0047, added by ADR 0127). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: a worker has no gateway to the process table's priorities yet (ADR 0072).
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class ReniceApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.renice";

    /// <inheritdoc />
    public override string Name => "renice";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(ReniceCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Alter the priority of running processes";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ProcessList, AppCapabilities.ProcessManage];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("renice", "renice [-n] N [-p] pid...");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.01,
        BaselineMemoryWeight: 0.0,
        BurstMemoryWeight: 0.01,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
