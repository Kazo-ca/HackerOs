using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Files;
using Kazo.Os.AppSdk.Icons;
using Kazo.Os.Apps.WebServer.Manager;

namespace Kazo.Os.Apps.WebServer;

/// <summary>
/// Manifest of "Web Server Manager", the IIS-like window that drives <c>hackwebd</c> through its
/// <c>.site</c> files and the service controller (ADR 0077, section 3). The build generates its
/// manifest from this class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class WebServerManagerApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <summary>The app id.</summary>
    public const string AppId = "org.hackeros.web-server-manager";

    /// <summary>
    /// The intent the OS hands for "Publish as a web site" on a folder (ADR 0151 §8), with
    /// <c>{"paths":["/home/neo/www"],"scope":"Directory"}</c>. Launching the app with the folder as its
    /// first argument (ADR 0077 §3) still works.
    /// </summary>
    public const string PublishSiteIntent = "org.hackeros.web-server-manager.publish-site.v1";

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => "Web Server Manager";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(WebServerManagerWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(WebServerManagerWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Publish folders as web sites and manage the hackwebd web server";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    /// <remarks>
    /// <c>filesystem.system.write</c> writes <c>/etc/hackweb/sites</c> (the files are
    /// <c>system:system</c>, so each write still needs an administrator's approval);
    /// <c>filesystem.system.read</c> reads the system sites and <c>/var/log/hackweb</c>;
    /// <c>apps.launch</c> opens a site in the browser; <c>process.list</c> shows which sockets
    /// <c>hackwebd</c> holds.
    /// </remarks>
    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.FileSystemSystemRead,
        AppCapabilities.FileSystemSystemWrite,
        AppCapabilities.FileSystemUserHomeRead,
        AppCapabilities.FileSystemUserHomeWrite,
        AppCapabilities.DialogFolderSelect,
        AppCapabilities.AppsLaunch,
        AppCapabilities.ProcessList,
        AppCapabilities.NotificationsPost,
        AppCapabilities.ShellContextMenu,
    ];

    /// <inheritdoc />
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(PublishSiteIntent)];

    /// <summary>
    /// Registers "Publish as a web site" on one folder, in the file menus of every host (ADR 0151 §8):
    /// the entry belongs to the app that does the work, no longer to the File Explorer. Run by the
    /// manifest generator at build, never at run time.
    /// </summary>
    /// <param name="context">Records the entries.</param>
    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Register(
            title: "Menu.PublishSite",
            name: "publish-site",
            intent: PublishSiteIntent,
            icon: HackIcons.Vector(IconLibrary.Lucide, "globe"),
            scope: FileMenuScope.Directory,
            selection: MenuSelection.Single);
    }

    /// <inheritdoc />
    public override bool SingleInstancePerUser => true;

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.04,
        BurstMemoryWeight: 0.1,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.05,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
