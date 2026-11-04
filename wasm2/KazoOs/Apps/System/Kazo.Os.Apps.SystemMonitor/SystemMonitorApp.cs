using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Apps.SystemMonitor;

/// <summary>
/// Manifest of "System Monitor" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class SystemMonitorApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.system-monitor";

    /// <inheritdoc />
    public override string Name => "System Monitor";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(SystemMonitorWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(SystemMonitorWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "Task manager: processes, CPU, memory, disk and network activity, and background services, read from /proc";

    /// <inheritdoc />
    public override string Category => "system";

    /// <summary>
    /// Reading <c>/proc</c> needs <c>filesystem.system.read</c>; seeing and ending other processes,
    /// <c>process.list</c> and <c>process.manage</c>; acting on services, <c>services.manage</c>
    /// (ADR 0056, sections 4 and 5).
    /// </summary>
    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.FileSystemSystemRead,
        AppCapabilities.ProcessList,
        AppCapabilities.ProcessManage,
        AppCapabilities.ServicesManage
    ];

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
