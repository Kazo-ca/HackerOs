using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Discovery;

namespace Kazo.Os.Platform.Core.Tests.Discovery;

/// <summary>Tests for <c>MOB-005</c>'s per-platform entry-point resolution step.</summary>
public sealed class AppPlatformEntryPointResolverTests
{
    [Fact]
    public void Resolve_returns_resolved_for_a_legacy_manifest_on_desktop()
    {
        AppManifest manifest = CreateManifest();

        AppPlatformEntryPointResolution resolution = AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Desktop);

        Assert.Equal(AppPlatformEntryPointResolutionStatus.Resolved, resolution.Status);
        Assert.True(resolution.IsResolved);
        Assert.Equal(manifest.EntryPoint, resolution.EntryPoint);
    }

    [Fact]
    public void Resolve_returns_platform_unsupported_for_a_legacy_manifest_on_mobile()
    {
        AppManifest manifest = CreateManifest();

        AppPlatformEntryPointResolution resolution = AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Mobile);

        Assert.Equal(AppPlatformEntryPointResolutionStatus.PlatformUnsupported, resolution.Status);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.EntryPoint);
    }

    [Fact]
    public void Resolve_returns_invalid_for_a_manifest_with_no_declaration()
    {
        AppManifest manifest = CreateManifest() with { EntryPoint = null, Platform = null };

        AppPlatformEntryPointResolution resolution = AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Desktop);

        Assert.Equal(AppPlatformEntryPointResolutionStatus.Invalid, resolution.Status);
        Assert.False(resolution.IsResolved);
    }

    [Fact]
    public void Resolve_finds_the_shared_entry_point_for_every_declared_platform()
    {
        AppManifest manifest = CreateManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["desktop", "mobile"],
                [new AppPlatformEntryPointManifest(["desktop", "mobile"], "Kazo.Os.Samples.PlatformApp.dll", "Kazo.Os.Samples.PlatformApp.PlatformDemoWindow")])
        };

        AppPlatformEntryPointResolution desktop = AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Desktop);
        AppPlatformEntryPointResolution mobile = AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Mobile);

        Assert.True(desktop.IsResolved);
        Assert.True(mobile.IsResolved);
        Assert.Equal(desktop.EntryPoint, mobile.EntryPoint);
    }

    private static AppManifest CreateManifest() => new()
    {
        Id = "org.hackeros.samples.platform-app",
        Name = "Platform Demo",
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "Resolver test application.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Samples.PlatformApp.dll", "Kazo.Os.Samples.PlatformApp.PlatformDemoWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };
}
