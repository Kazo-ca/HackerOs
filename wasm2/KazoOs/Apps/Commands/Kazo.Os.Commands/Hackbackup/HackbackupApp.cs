using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Hackbackup;

/// <summary>
/// Manifest of "hackbackup" (ADR 0099, section 1). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: the browser's download and file picker belong to the page,
// and the picker only opens shortly after the Enter key that started the command.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class HackbackupApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.hackbackup";

    /// <inheritdoc />
    public override string Name => "hackbackup";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(HackbackupCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Back up the whole machine to the host computer, or restore it";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.SystemBackup];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("hackbackup", "hackbackup create | restore [--merge|--replace]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.2,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
