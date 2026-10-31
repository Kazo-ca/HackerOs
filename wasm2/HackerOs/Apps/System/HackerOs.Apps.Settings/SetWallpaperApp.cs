using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Icons;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Manifest of « Set as wallpaper » (ADR 0218 §1): a hidden service app declared by Settings that
/// owns the entries of the image menus and writes the chosen image to <c>appearance.config</c>. The
/// build generates its manifest from this class; it is never instantiated at runtime.
/// </summary>
/// <remarks>
/// It runs on the main thread: each click is a task of an instant (read the image, write one key),
/// and a dedicated worker would start a .NET runtime for every click.
/// </remarks>
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class SetWallpaperApp : AppDefinition
{
    /// <summary>The app id.</summary>
    public const string AppId = "org.hackeros.settings.wallpaper";

    /// <summary>
    /// The intent the OS hands for every entry, with <c>{"paths":["/home/neo/a.png"],"scope":"File","entry":"set-wallpaper-screen-2"}</c>:
    /// the entry's name says which screen (ADR 0218 §3).
    /// </summary>
    public const string SetWallpaperIntent = "org.hackeros.settings.set-wallpaper.v1";

    /// <summary>The entry shown with one screen: the image becomes the wallpaper of every screen.</summary>
    public const string SingleEntry = "set-wallpaper";

    /// <summary>The submenu shown with several screens.</summary>
    public const string SubmenuEntry = "set-wallpaper-on";

    /// <summary>The submenu's entry for screen 1 and every screen without its own wallpaper.</summary>
    public const string AllScreensEntry = "set-wallpaper-all";

    /// <summary>The prefix of the submenu's entry of one secondary screen, followed by its number.</summary>
    public const string ScreenEntryPrefix = "set-wallpaper-screen-";

    /// <summary>The image types the entries apply to, the formats of ADR 0202 §3.</summary>
    public static readonly IReadOnlyList<string> ImageTypes = [".png", ".jpg", ".jpeg", ".webp", "image/png", "image/jpeg", "image/webp"];

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => "Set as wallpaper";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Service;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(SetWallpaperService);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Sets an image as the desktop wallpaper from the file menus";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    /// <remarks>
    /// The home and system read capabilities read the image as the session user (an image of
    /// <c>/usr/share</c> as well as one of the home); writing the user layer of
    /// <c>appearance.config</c> needs no capability (the file's permissions decide).
    /// </remarks>
    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.FileSystemUserHomeRead,
        AppCapabilities.FileSystemUserHomeWrite,
        AppCapabilities.FileSystemSystemRead,
        AppCapabilities.NotificationsPost,
        AppCapabilities.ShellContextMenu,
    ];

    /// <inheritdoc />
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(SetWallpaperIntent)];

    /// <summary>
    /// Registers « Set as wallpaper » on one image, right after « Open with » (ADR 0218 §4): a plain
    /// entry with one screen, a submenu of the open screens with several. Run by the manifest
    /// generator at build, never at run time.
    /// </summary>
    /// <param name="context">Records the entries.</param>
    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        HackIcon icon = HackIcons.Vector(IconLibrary.Lucide, "image");
        context.Register(
            title: "Menu.SetWallpaper",
            name: SingleEntry,
            intent: SetWallpaperIntent,
            icon: icon,
            types: ImageTypes,
            selection: MenuSelection.Single,
            after: HackFileMenuItems.OpenWith);
        context.RestrictToScreens(SingleEntry, MenuScreens.Single);

        context.Register(
            title: "Menu.SetWallpaper",
            name: SubmenuEntry,
            icon: icon,
            types: ImageTypes,
            selection: MenuSelection.Single,
            after: HackFileMenuItems.OpenWith);
        context.RestrictToScreens(SubmenuEntry, MenuScreens.Multiple);

        context.Register(title: "Menu.AllScreens", name: AllScreensEntry, intent: SetWallpaperIntent, parent: SubmenuEntry);
        for (int screen = AppContextMenus.MinScreenNumber; screen <= AppContextMenus.MaxScreenNumber; screen++)
        {
            string name = ScreenEntryPrefix + screen.ToString(System.Globalization.CultureInfo.InvariantCulture);
            context.Register(title: $"Menu.Screen{screen}", name: name, intent: SetWallpaperIntent, parent: SubmenuEntry);
            context.RestrictToScreen(name, screen);
        }
    }

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.05,
        BaselineMemoryWeight: 0.0,
        BurstMemoryWeight: 0.05,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.05,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
