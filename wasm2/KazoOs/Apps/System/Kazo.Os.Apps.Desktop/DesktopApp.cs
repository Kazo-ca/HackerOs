using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Files;

namespace Kazo.Os.Apps.Desktop;

/// <summary>
/// Manifest of the desktop (ADR 0160 §1). The build generates <c>manifest.json</c> from this class; it
/// is never instantiated at runtime.
/// </summary>
/// <remarks>
/// The desktop is an app like the others, with the rights of the signed-in user only (public SDK, no
/// system right): the shell starts it at sign-in and draws it under the windows, never in a window of
/// its own. Hidden from the Start menu, one per user. "Send to ▸ Desktop" reaches the running instance
/// as the <see cref="KazoDesktopItems.CreateShortcutIntent"/> intent.
/// </remarks>
[AppManifest]
public sealed class DesktopApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.desktop";

    /// <inheritdoc />
    public override string Name => "Desktop";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(DesktopSurface);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "The desktop: the icons of ~/Desktop, Home and Trash";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override bool SingleInstancePerUser => true;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.AppsLaunch,
        AppCapabilities.FileSystemUserHomeRead,
        AppCapabilities.FileSystemUserHomeWrite,
        AppCapabilities.ClipboardRead,
        AppCapabilities.ClipboardWrite,
        AppCapabilities.NotificationsPost,
        AppCapabilities.FileAssociationsRead
    ];

    /// <inheritdoc />
    public override IReadOnlyList<AppIntentDeclarationManifest> Intents => [new(KazoDesktopItems.CreateShortcutIntent)];

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.03,
        BurstMemoryWeight: 0.1,
        BaselineStorageIoWeight: 0.02,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
