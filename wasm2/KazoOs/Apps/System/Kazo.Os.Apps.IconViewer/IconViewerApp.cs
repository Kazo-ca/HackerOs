using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Apps.IconViewer;

/// <summary>
/// Manifest of "Icon Viewer" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class IconViewerApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.kazoos.icon-viewer";

    /// <inheritdoc />
    public override string Name => "Icon Viewer";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(IconViewerWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(IconViewerWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description =>
        "Browse, search, and copy usage snippets for every icon bundled with Kazo OS: Bootstrap Icons, Font Awesome, Lucide, and Simple Icons";

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.05,
        BurstMemoryWeight: 0.1,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
