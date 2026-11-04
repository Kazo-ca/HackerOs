using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Exit;

/// <summary>
/// Manifest of "cd" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it ends the calling shell, like the exit builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class ExitApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.exit";

    /// <inheritdoc />
    public override string Name => "exit";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(ExitCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Exit the shell";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("exit", "exit [n]");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.01,
        BurstStorageIoWeight: 0.05,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
