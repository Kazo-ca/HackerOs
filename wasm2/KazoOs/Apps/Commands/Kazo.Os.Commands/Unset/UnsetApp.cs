using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Unset;

/// <summary>
/// Manifest of "unset" (ADR 0047). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it removes the calling shell's variables, like the unset builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class UnsetApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.unset";

    /// <inheritdoc />
    public override string Name => "unset";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(UnsetCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Remove environment variables from the shell";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("unset", "unset NAME...");

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
