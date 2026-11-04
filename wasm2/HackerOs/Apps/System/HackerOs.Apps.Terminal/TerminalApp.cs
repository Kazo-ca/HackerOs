using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Components;

namespace HackerOs.Apps.Terminal;

/// <summary>
/// Manifest of "Terminal" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class TerminalApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.terminal";

    /// <inheritdoc />
    public override string Name => "Terminal";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(TerminalWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(TerminalWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Command line interface and interactive shell for KazoOS";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.AppsLaunch, AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.FileSystemSystemRead, AppCapabilities.ProcessList, AppCapabilities.ProcessManage, AppCapabilities.NotificationsPost, AppCapabilities.ShellHost];

    /// <inheritdoc />
    public override bool SingleInstancePerUser => true;

    /// <summary>
    /// The terminal other apps embed (ADR 0063), e.g. the Code Editor's panel: the same panel as
    /// this window, controlled through <c>IHackTerminal</c>.
    /// </summary>
    public override IReadOnlyList<AppComponent> Components =>
        [new(HackComponentContracts.Terminal, typeof(TerminalEmbed))];

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.05,
        BurstCpuWeight: 0.3,
        BaselineMemoryWeight: 0.05,
        BurstMemoryWeight: 0.2,
        BaselineStorageIoWeight: 0.02,
        BurstStorageIoWeight: 0.1,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
