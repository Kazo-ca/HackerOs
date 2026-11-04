using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Apps.TextEditor;

/// <summary>
/// Manifest of "Text Editor" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class TextEditorApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.kazoos.text-editor";

    /// <inheritdoc />
    public override string Name => "Text Editor";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(TextEditorWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(TextEditorWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Authorized virtual-filesystem text editor for Kazo OS";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.AppsLaunch, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.DialogFileOpen, AppCapabilities.DialogFileSave, AppCapabilities.NotificationsPost];

    /// <inheritdoc />
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new("text/plain", [".txt", ".md", ".log", ".json", ".csv", ".ini", ".conf", ".cfg", ".yml", ".yaml", ".xml", ".env"], ["open", "edit"])];

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.04,
        BurstMemoryWeight: 0.15,
        BaselineStorageIoWeight: 0.02,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
