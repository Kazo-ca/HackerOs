using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Blazor.Files;
using HackerOs.AppSdk.Files;
using HackerOs.AppSdk.Icons;

namespace Contoso.Sdk2Compat;

/// <summary>Manifest of the window app (generated into its manifest file at build).</summary>
[AppManifest]
public sealed class CompatWindowApp : AppDefinition
{
    /// <summary>The intent of the "Inspect with SDK 2.0" file menu entry.</summary>
    public const string InspectIntent = "com.contoso.sdk2.inspect.v1";

    /// <inheritdoc />
    public override string Id => "com.contoso.sdk2-window";

    /// <inheritdoc />
    public override string Name => "SDK 2.0 Window";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CompatWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.contoso";

    /// <inheritdoc />
    public override string Description => "A window app compiled against Kazo OS SDK 2.0.0 that shows the OS file control.";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.FileSystemUserHomeRead,
        AppCapabilities.FileSystemUserHomeWrite,
        AppCapabilities.ShellContextMenu
    ];

    /// <inheritdoc />
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(InspectIntent)];

    /// <summary>
    /// SDK 2.0 (ADR 0151): registers "Inspect with SDK 2.0" on one file, in the file menus of every
    /// host. Run by the manifest generator at build, never at run time.
    /// </summary>
    /// <param name="context">Records the entries.</param>
    [RegisterFileContextMenu]
    public static void RegisterFileContextMenu(RegisterFileContextMenuContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Register(
            title: "Menu.Inspect",
            name: "inspect",
            intent: InspectIntent,
            icon: HackIcons.Vector(IconLibrary.Lucide, "search"),
            selection: MenuSelection.Single,
            afterGroup: HackFileMenuItems.Copy);
    }
}
