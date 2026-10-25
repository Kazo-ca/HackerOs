using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Components;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Icons;

namespace HackerOs.Apps.FileExplorer;

/// <summary>
/// Manifest of "File Explorer" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class FileExplorerApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.file-explorer";

    /// <inheritdoc />
    public override string Name => "File Explorer";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(FileExplorerWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(FileExplorerMobileWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Graphical virtual file manager and directory browser";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.AppsLaunch, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite, AppCapabilities.DialogFileOpen, AppCapabilities.DialogFileSave, AppCapabilities.DialogFolderSelect, AppCapabilities.ClipboardRead, AppCapabilities.ClipboardWrite, AppCapabilities.NotificationsPost, AppCapabilities.FileAssociationsRead, AppCapabilities.ShellContextMenu];

    /// <summary>"Extract here" (ADR 0151 §8): the OS hands the paths of the archives with this intent.</summary>
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(FileExplorerMenuEntries.ExtractHereIntent)];

    /// <inheritdoc />
    public override IReadOnlyList<FileHandler> FileHandlers => [new("inode/directory", [], ["open"])];

    /// <summary>
    /// The OS file control and its dialogs (ADR 0148, ADR 0155): every app shows the explorer's
    /// <c>HackFileView</c> and opens its "Open with", properties and conflict dialogs; the explorer
    /// shows them itself.
    /// </summary>
    public override IReadOnlyList<AppComponent> Components =>
    [
        new(HackComponentContracts.FileView, typeof(ExplorerFileView)),
        new(HackComponentContracts.FileDialogs, typeof(ExplorerFileDialogs))
    ];

    /// <summary>
    /// Registers "Extract here" on <c>.zip</c> files, right after "Open", in the file menus of every
    /// host: the explorer, the desktop, the file dialogs and any app showing <c>HackFileView</c>
    /// (ADR 0151 §8). Run by the manifest generator at build, never at run time.
    /// </summary>
    /// <param name="context">Records the entries.</param>
    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Register(
            title: FileExplorerMenuEntries.ExtractHereTitle,
            name: FileExplorerMenuEntries.ExtractHere,
            intent: FileExplorerMenuEntries.ExtractHereIntent,
            icon: HackIcons.Vector(IconLibrary.Lucide, "package-open"),
            types: [".zip", "application/zip"],
            after: HackFileMenuItems.Open);
    }

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.05,
        BurstCpuWeight: 0.2,
        BaselineMemoryWeight: 0.05,
        BurstMemoryWeight: 0.2,
        BaselineStorageIoWeight: 0.05,
        BurstStorageIoWeight: 0.3,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
