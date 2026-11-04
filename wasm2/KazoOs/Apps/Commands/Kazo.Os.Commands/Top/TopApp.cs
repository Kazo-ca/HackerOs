using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Top;

/// <summary>
/// Manifest of "top" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it draws on the terminal's full screen (alternate screen and raw keys), a main-thread device.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class TopApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.top";

    /// <inheritdoc />
    public override string Name => "top";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(TopCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Display the processes using the most CPU, refreshed live";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ProcessList, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("top", "top [-b] [-n iterations] [-d seconds] [-u user]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.2,
        BaselineMemoryWeight: 0.02,
        BurstMemoryWeight: 0.1,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.05,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
