using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Samples.WindowApp;

/// <summary>
/// Manifest of "Sample Window App" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class WindowSampleApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.samples.window-app";

    /// <inheritdoc />
    public override string Name => "Sample Window App";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(SampleWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description =>
        "Complete sample Window application demonstrating App SDK features, scoped assets, intents, settings, and dialogs";

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities =>
        [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite, AppCapabilities.DialogFileOpen, AppCapabilities.DialogFileSave];

    /// <inheritdoc />
    public override bool SingleInstancePerUser => true;

    /// <inheritdoc />
    /// <remarks>ADR 0095 §2: opens at 720×520 and cannot be made smaller than 480×360.</remarks>
    public override WindowOptions Window => new()
    {
        DefaultWidth = 720,
        DefaultHeight = 520,
        MinWidth = 480,
        MinHeight = 360,
    };

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
