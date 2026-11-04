using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Which;

/// <summary>
/// Manifest of "which" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it resolves through the calling shell's builtins and PATH, like the type builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class WhichApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.which";

    /// <inheritdoc />
    public override string Name => "which";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(WhichCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Locate a command";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("which", "which [-a] name...");

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
