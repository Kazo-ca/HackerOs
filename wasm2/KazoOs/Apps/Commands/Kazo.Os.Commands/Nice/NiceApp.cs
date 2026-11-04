using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Nice;

/// <summary>
/// Manifest of "nice" (ADR 0047, added by ADR 0127). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it runs its command through the calling shell (RunNicedAsync), as sudo does.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class NiceApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.nice";

    /// <inheritdoc />
    public override string Name => "nice";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(NiceCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Run a command with modified scheduling priority";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("nice", "nice [-n N] [command [args...]]");

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
