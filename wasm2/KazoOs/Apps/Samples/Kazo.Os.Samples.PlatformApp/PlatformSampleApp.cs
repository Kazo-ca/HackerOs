using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Samples.PlatformApp;

/// <summary>
/// Manifest of "Platform Demo" (ADR 0047). The build generates <c>manifest.json</c> from this
/// class; it is never instantiated at runtime.
/// </summary>
[AppManifest]
public sealed class PlatformSampleApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.samples.platform-app";

    /// <inheritdoc />
    public override string Name => "Platform Demo";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(PlatformDemoWindow);

    /// <summary>
    /// One shared entry point covering both platforms (plan §4.1's first example): a single
    /// component reused across <c>desktop</c> and <c>mobile</c> rather than one variant per platform.
    /// </summary>
    public override Type? MobileEntryPoint => typeof(PlatformDemoWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description =>
        "Demonstrates a manifest with a shared entry point covering multiple platforms.";
}
