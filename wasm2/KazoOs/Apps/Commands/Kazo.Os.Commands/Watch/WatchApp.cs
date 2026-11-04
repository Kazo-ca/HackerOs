using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Watch;

/// <summary>
/// Manifest of "watch" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it re-runs command lines through the calling shell and draws on the terminal's full screen.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class WatchApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.watch";

    /// <inheritdoc />
    public override string Name => "watch";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(WatchCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Run a command repeatedly, showing its output full screen";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("watch", "watch [-n seconds] [-t] [-g] [-e] command");

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
