using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Apps.CodeEditor;

/// <summary>
/// Manifest of "Code Editor" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class CodeEditorApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.code-editor";

    /// <inheritdoc />
    public override string Name => "Code Editor";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CodeEditorWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(CodeEditorWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "Multi-tab code and text editor with syntax mode selection, line numbering, and VFS integration";

    /// <inheritdoc />
    public override string Category => "developer";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.DialogFileOpen, AppCapabilities.DialogFileSave, AppCapabilities.DialogFolderSelect, AppCapabilities.ClipboardRead, AppCapabilities.ClipboardWrite, AppCapabilities.AppsLaunch, AppCapabilities.ShellHost];

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
