using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Help;

/// <summary>
/// Manifest of "help" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it lists the calling shell's commands, like the help builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class HelpApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.help";

    /// <inheritdoc />
    public override string Name => "help";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(HelpCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Display help and manual pages for commands";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("help", "help [command]", ["man"]);

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
