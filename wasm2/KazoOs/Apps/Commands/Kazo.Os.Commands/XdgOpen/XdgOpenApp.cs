using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.XdgOpen;

/// <summary>
/// Manifest of "xdg-open" (ADR 0096, section 3). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class XdgOpenApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.xdg-open";

    /// <inheritdoc />
    public override string Name => "xdg-open";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(XdgOpenCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Open a file, folder or URI in the user's preferred application";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    /// <remarks>Reading is needed to tell a missing file (exit 4) from a folder.</remarks>
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.AppsLaunch, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("xdg-open", "xdg-open <file|folder|URI>");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.01,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.01,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
