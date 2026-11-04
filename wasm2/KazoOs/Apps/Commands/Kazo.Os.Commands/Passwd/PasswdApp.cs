using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Passwd;

/// <summary>
/// Manifest of "passwd" (ADR 0120). The build generates <c>manifests/&lt;appId&gt;.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: it reads the terminal with echo off (ADR 0120 section 4), and a worker's input is not the terminal.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class PasswdApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.passwd";

    /// <inheritdoc />
    public override string Name => "passwd";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(PasswdCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Change user password";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.UsersManage];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("passwd", "passwd [-d] [-S] [user]");

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
