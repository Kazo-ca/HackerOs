using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Htop;

/// <summary>
/// Manifest of "htop" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it draws on the terminal's full screen (alternate screen and raw keys), a main-thread device.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class HtopApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.htop";

    /// <inheritdoc />
    public override string Name => "htop";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(HtopCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Interactive process viewer with meters, sorting and kill";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ProcessList, AppCapabilities.ProcessManage, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("htop", "htop [-d tenths] [-u user]");

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
