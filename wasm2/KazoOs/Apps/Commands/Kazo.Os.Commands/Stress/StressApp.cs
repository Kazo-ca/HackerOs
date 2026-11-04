using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Stress;

/// <summary>
/// Manifest of "stress" (ADR 0070). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime. Runs in the worker pool (the default of a command):
/// its busy loop occupies a virtual core, never the desktop.
/// </summary>
[AppManifest]
public sealed class StressApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.stress";

    /// <inheritdoc />
    public override string Name => "stress";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(StressCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Impose a CPU or memory load on the system";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("stress", "stress [-c N] [-m N] [--vm-bytes B] [--vm-keep] [-t SECONDS] [--yield]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 1.0,
        BurstCpuWeight: 1.0,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.01,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
