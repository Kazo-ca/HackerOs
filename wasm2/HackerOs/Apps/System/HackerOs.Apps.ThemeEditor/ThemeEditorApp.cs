using HackerOs.App.Abstractions;
using HackerOs.Theming.Abstractions;

namespace HackerOs.Apps.ThemeEditor;

/// <summary>
/// Manifest of "Theme Editor" (ADR 0103 §2.1). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class ThemeEditorApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.theme-editor";

    /// <inheritdoc />
    public override string Name => "Theme Editor";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(ThemeEditorWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(ThemeEditorWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "Create KazoOS themes from verified design tokens, with live preview and token documentation";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    /// <remarks>
    /// Home read/write for <c>~/.config/hackeros/themes</c>; system read to list the themes an
    /// administrator installed in <c>/etc/hackeros/themes</c>; dialogs to open and export files
    /// anywhere else.
    /// </remarks>
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.FileSystemSystemRead, AppCapabilities.DialogFileOpen, AppCapabilities.DialogFileSave];

    /// <inheritdoc />
    public override IReadOnlyList<FileHandler> FileHandlers =>
        [new(ThemeFile.MediaType, [ThemeFile.Extension], ["open", "edit"])];

    /// <inheritdoc />
    public override WindowOptions? Window => new()
    {
        DefaultWidth = 1180,
        DefaultHeight = 760,
        MinWidth = 820,
        MinHeight = 540
    };

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.04,
        BurstMemoryWeight: 0.1,
        BaselineStorageIoWeight: 0.01,
        BurstStorageIoWeight: 0.05,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
