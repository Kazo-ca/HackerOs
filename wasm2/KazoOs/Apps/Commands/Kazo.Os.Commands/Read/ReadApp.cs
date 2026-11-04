using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Read;

/// <summary>
/// Manifest of "read" (ADR 0047, added by ADR 0064). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it assigns the calling shell's variables, like the read builtin of bash.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class ReadApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.read";

    /// <inheritdoc />
    public override string Name => "read";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(ReadCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Read a line from standard input into shell variables";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.ShellControl];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("read", "read [-r] [-p prompt] [-s] [-n count] [-t seconds] [-d delim] [name...]");

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
