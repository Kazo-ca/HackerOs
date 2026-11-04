using HackerOs.App.Abstractions;

namespace HackerOs.Apps.Diagnostic;

/// <summary>
/// Manifest of "IndexedDB Diagnostic" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class DiagnosticApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.diagnostic";

    /// <inheritdoc />
    public override string Name => "IndexedDB Diagnostic";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(DiagnosticWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(DiagnosticWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "System Diagnostic and IndexedDB Inspector for Kazo OS storage inspection and file path resolution";

    /// <inheritdoc />
    public override string Category => "system";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.SystemStorageInspect, AppCapabilities.ClipboardWrite, AppCapabilities.DialogFileSave, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite];

    /// <inheritdoc />
    public override bool SingleInstancePerUser => true;

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.02,
        BurstCpuWeight: 0.1,
        BaselineMemoryWeight: 0.05,
        BurstMemoryWeight: 0.15,
        BaselineStorageIoWeight: 0.05,
        BurstStorageIoWeight: 0.2,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
