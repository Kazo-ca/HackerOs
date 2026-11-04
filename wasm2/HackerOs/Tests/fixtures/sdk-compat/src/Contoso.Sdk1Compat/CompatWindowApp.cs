using HackerOs.App.Abstractions;

namespace Contoso.Sdk1Compat;

/// <summary>Manifest of the window app (generated into its manifest file at build).</summary>
[AppManifest]
public sealed class CompatWindowApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "com.contoso.sdk1-window";

    /// <inheritdoc />
    public override string Name => "SDK 1.0 Window";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CompatWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.contoso";

    /// <inheritdoc />
    public override string Description => "A window app compiled against KazoOS SDK 1.0.0.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];
}
