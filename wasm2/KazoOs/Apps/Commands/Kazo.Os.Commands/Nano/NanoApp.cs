using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Nano;

/// <summary>
/// Manifest of "nano" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it draws on the terminal's full screen (alternate screen and raw keys), a main-thread device.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class NanoApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.nano";

    /// <inheritdoc />
    public override string Name => "nano";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(NanoCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Terminal interactive text editor command";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("nano", "nano [file]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.02,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
