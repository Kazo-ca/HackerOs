using HackerOs.App.Abstractions;

namespace HackerOs.Apps.Store;

/// <summary>
/// Manifest of the Hack Store (ADR 0188 §1): the system app that lists the apps of the catalogs of
/// <c>/etc/hackeros/app-sources.config</c>, shows their page, and installs, updates and removes
/// them. The build generates <c>manifest.json</c> from this class; it is never instantiated at
/// runtime.
/// </summary>
/// <remarks>
/// A system app (the host references it with <c>IsSystemApp="true"</c>), so it cannot be
/// uninstalled, like Settings. It uses only the public SDK: <c>HackAppPackages</c> (<c>apps.manage</c>,
/// dangerous, granted to system apps without a prompt) and <c>HackApps.LaunchAsync</c>
/// (<c>apps.launch</c>) for « Open ».
/// </remarks>
[AppManifest]
public sealed class StoreApp : AppDefinition
{
    /// <summary>The app ID, also the one the OS pins by default and draws the update count on.</summary>
    public const string AppId = "org.hackeros.store";

    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => "Hack Store";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(StoreWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(StoreWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Find, install, update and remove apps from the reviewed HackerOS catalogs";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.AppsManage, AppCapabilities.AppsLaunch];

    /// <inheritdoc />
    public override bool SingleInstancePerUser => true;

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.05,
        BurstMemoryWeight: 0.15,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.1,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.2);
}
