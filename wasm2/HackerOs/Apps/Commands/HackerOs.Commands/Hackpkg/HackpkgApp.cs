using HackerOs.App.Abstractions;

namespace HackerOs.Commands.Hackpkg;

/// <summary>
/// Manifest of "hackpkg" (ADR 0182, section 4). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: the package is written by the OS of the page, and the
// administrator prompt opens there.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class HackpkgApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.commands.hackpkg";

    /// <inheritdoc />
    public override string Name => "hackpkg";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(HackpkgCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Install, update and remove apps from their GitHub repository";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.AppsManage];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new(
        "hackpkg",
        "hackpkg install <repo>[@version] [--allow-untrusted] | remove <appId> | update [<appId>] | list | info <repo> | sources");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.2,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.2);
}
