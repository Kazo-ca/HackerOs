using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Kazopkg;

/// <summary>
/// Manifest of "kazopkg" (ADR 0182, section 4). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread: the package is written by the OS of the page, and the
// administrator prompt opens there.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class KazopkgApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.kazopkg";

    /// <inheritdoc />
    public override string Name => "kazopkg";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(KazopkgCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Install, update and remove apps from their GitHub repository";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.AppsManage];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new(
        "kazopkg",
        "kazopkg install <repo>[@version] [--user|--system] | remove <appId> [--user|--system] | update [<appId>] | list | info <repo> | sources [add|remove <source>] | policy",
        // ADR 0228 §2: the name before the rename stays an alias; the shell warns when it is used.
        ["hackpkg"]);

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
