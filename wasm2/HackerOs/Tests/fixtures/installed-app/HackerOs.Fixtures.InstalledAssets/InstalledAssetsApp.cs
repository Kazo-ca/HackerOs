using HackerOs.App.Abstractions;

namespace HackerOs.Fixtures.InstalledAssets;

/// <summary>
/// Manifest of the test package of ADR 0206: a window app whose <c>wwwroot/</c> files exist only
/// once it is installed by <c>hackpkg</c>. The SHA-256 values are computed by the build.
/// </summary>
/// <remarks>
/// ADR 0221: <c>engine/engine.js</c>, <c>ProbeWindow.razor.js</c> and the scoped CSS bundle are not
/// declared here; <c>HackerOsPack</c> adds them to the package's manifest.
/// </remarks>
[AppManifest]
public sealed class InstalledAssetsApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.hackeros.fixtures.installed-assets";

    /// <inheritdoc />
    public override string Name => "Installed Assets Probe";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(ProbeWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.hackeros";

    /// <inheritdoc />
    public override string Description => "Test package: loads a module, a Web Worker and an image from its wwwroot/ once installed.";

    /// <inheritdoc />
    public override IReadOnlyList<AssetManifest> Assets =>
    [
        new("probe.js", AssetKind.JavaScriptModule, new string('0', 64)),
        new("engine/worker.js", AssetKind.JavaScriptModule, new string('0', 64)),
        new("images/dot.svg", AssetKind.Image, new string('0', 64)),
    ];
}
