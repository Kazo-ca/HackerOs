using HackerOs.App.Abstractions;

namespace HackerOs.Apps.Settings;

/// <summary>
/// Manifest of "Settings" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class SettingsApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.settings";

    /// <inheritdoc />
    public override string Name => "Settings";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(SettingsWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(SettingsWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "System control panel for user preferences, appearance, language, file associations, capabilities, backup, and device configuration";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.AppsLaunch, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite, AppCapabilities.FileAssociationsRead, AppCapabilities.FileAssociationsWrite, AppCapabilities.ProcessList, AppCapabilities.ServicesManage, AppCapabilities.AppsManage, AppCapabilities.PermissionsManage, AppCapabilities.ServerConnectionManage, AppCapabilities.SystemPower, AppCapabilities.SystemBackup, AppCapabilities.UsersManage, AppCapabilities.ScreensManage];

    /// <inheritdoc />
    public override bool SingleInstancePerUser => true;

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
