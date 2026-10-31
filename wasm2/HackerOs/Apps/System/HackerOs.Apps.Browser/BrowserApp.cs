using HackerOs.App.Abstractions;

namespace HackerOs.Apps.Browser;

/// <summary>
/// Manifest of "Browser" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class BrowserApp : AppDefinition
{
    /// <inheritdoc />
    public override IReadOnlyList<string> Icons => ["icons/app.svg"];

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
        [new("icons/app.svg", AssetKind.Image, new string('0', 64), "app-icon")];
    /// <inheritdoc />
    public override string Id => "org.hackeros.browser";

    /// <inheritdoc />
    public override string Name => "Browser";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(BrowserWindow);

    /// <inheritdoc />
    public override Type? MobileEntryPoint => typeof(BrowserWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description =>
        "Web browser for the HackerOS virtual network and, through a connected server, the real Internet";

    /// <inheritdoc />
    public override string Category => "internet";

    /// <inheritdoc />
    /// <remarks>
    /// Files (ADR 0075 §4 and §5): <c>file://</c> pages are read in the user's home with the user's
    /// rights; downloads go to <c>~/Downloads</c> and <c>localStorage</c> to
    /// <c>~/.local/share/hackeros/browser/storage</c>. The browser does not ask for
    /// <c>filesystem.system.read</c>: a <c>file://</c> URL outside the home answers <c>ERR_ACCESS_DENIED</c>.
    /// Real sites (ADR 0210 §1): <c>network.real.access</c> lets the OS relay a host unknown to the
    /// simulated network through the optional server; the host makes the browser a system app so this
    /// dangerous capability needs no approval prompt, as for <c>curl</c> and <c>wget</c>.
    /// </remarks>
    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.NetworkSimulatedRead,
        AppCapabilities.NetworkSimulatedWrite,
        AppCapabilities.NetworkRealAccess,
        AppCapabilities.FileSystemUserHomeRead,
        AppCapabilities.FileSystemUserHomeWrite,
    ];

    /// <inheritdoc />
    /// <remarks>
    /// Opening an <c>.html</c> file shows it as <c>file://</c> (ADR 0075 §4). The browser is also the
    /// handler of <c>http:</c> and <c>https:</c> links, declared as freedesktop does with the media
    /// types <c>x-scheme-handler/&lt;scheme&gt;</c> (<c>OpenUriIntent</c>, <c>xdg-open</c>, ADR 0096 §3).
    /// </remarks>
    public override IReadOnlyList<FileHandler> FileHandlers =>
    [
        new("text/html", [".html", ".htm"], ["open"]),
        new("x-scheme-handler/http", [], ["open"]),
        new("x-scheme-handler/https", [], ["open"])
    ];

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.05,
        BurstCpuWeight: 0.25,
        BaselineMemoryWeight: 0.08,
        BurstMemoryWeight: 0.3,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.05,
        BaselineNetworkIoWeight: 0.05,
        BurstNetworkIoWeight: 0.2);
}
