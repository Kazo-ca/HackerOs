using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Export;

/// <summary>
/// Manifest of "export" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it sets the calling shell's variables, like the export builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class ExportApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.export";

    /// <inheritdoc />
    public override string Name => "export";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(ExportCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Set environment variables of the shell";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("export", "export [NAME=value...]");

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
