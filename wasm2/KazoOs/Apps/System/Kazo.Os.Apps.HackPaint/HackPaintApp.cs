using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Apps.HackPaint;

/// <summary>
/// Manifest of "Hack Paint" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class HackPaintApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.hack-paint";

    /// <inheritdoc />
    public override string Name => "Hack Paint";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(HackPaintWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(HackPaintWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "Image editor that opens and saves PNG, JPEG, BMP, GIF and WebP files, with a drawing canvas and undo/redo";

    /// <inheritdoc />
    public override string Category => "graphics";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.DialogFileOpen, AppCapabilities.DialogFileSave];

    /// <inheritdoc />
    /// <remarks>
    /// ADR 0103 §1: Hack Paint opens and edits the image types the browser decodes; it is the
    /// default handler of <c>edit</c> for them (<c>/etc/hackeros/file-associations.json</c>).
    /// </remarks>
    public override IReadOnlyList<FileHandler> FileHandlers =>
    [
        new("image/png", [".png"], ["open", "edit"]),
        new("image/jpeg", [".jpg", ".jpeg"], ["open", "edit"]),
        new("image/bmp", [".bmp"], ["open", "edit"]),
        new("image/gif", [".gif"], ["open", "edit"]),
        new("image/webp", [".webp"], ["open", "edit"])
    ];

    /// <inheritdoc />
    public override WindowOptions? Window => new()
    {
        DefaultWidth = 960,
        DefaultHeight = 680,
        MinWidth = 600,
        MinHeight = 420
    };

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.05,
        BurstCpuWeight: 0.25,
        BaselineMemoryWeight: 0.08,
        BurstMemoryWeight: 0.3,
        BaselineStorageIoWeight: 0.05,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
