using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.XdgMime;

/// <summary>
/// Manifest of "xdg-mime" (ADR 0121, section 5). The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class XdgMimeApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.xdg-mime";

    /// <inheritdoc />
    public override string Name => "xdg-mime";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(XdgMimeCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Query and set the user's default applications for file types and URI schemes";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    /// <remarks>
    /// <c>file-associations.read</c> reads both layers, <c>filesystem.user-home.write</c> writes the
    /// user's own file, and the two reads tell a folder (<c>inode/directory</c>) from a file.
    /// </remarks>
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileAssociationsRead, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.FileSystemSystemRead];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("xdg-mime", "xdg-mime query { default <type> | filetype <file> } | default <app-id> <type>...");

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
