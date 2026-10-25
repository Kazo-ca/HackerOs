using HackerOs.App.Abstractions;

namespace HackerOs.App.Abstractions.Tests;

/// <summary>
/// Covers the multi-platform manifest declaration (<c>MOB-003</c>/<c>MOB-004</c>): validation rules
/// from doc/en/wasm/dev/old/mobile-interface-platform-plan.md §4.2, and the
/// <see cref="AppManifestPlatformSupport.Resolve"/> normalizer that both
/// <see cref="AppManifestValidator"/> and the entry-point resolver build on.
/// </summary>
public sealed class AppManifestPlatformTests
{
    [Fact]
    public void Validate_rejects_a_manifest_with_neither_entryPoint_nor_platform()
    {
        AppManifest manifest = CreateBaseManifest() with { EntryPoint = null, Platform = null };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.platform.required");
    }

    [Fact]
    public void Validate_rejects_a_manifest_declaring_both_entryPoint_and_platform()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = new AppEntryPointManifest("HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.PlatformDemoWindow"),
            Platform = new AppManifestPlatform(
                ["desktop"],
                [new AppPlatformEntryPointManifest(["desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.PlatformDemoWindow")])
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.platform.ambiguous");
    }

    [Fact]
    public void Validate_accepts_a_shared_entry_point_covering_multiple_platforms()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["desktop", "mobile"],
                [new AppPlatformEntryPointManifest(["desktop", "mobile"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.PlatformDemoWindow")])
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_accepts_two_platform_specific_entry_points_under_one_manifest()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["desktop", "mobile"],
                [
                    new AppPlatformEntryPointManifest(["desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.DesktopWindow"),
                    new AppPlatformEntryPointManifest(["mobile"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.MobileWindow")
                ])
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_rejects_a_supported_platform_with_no_covering_entry_point()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["desktop", "mobile"],
                [new AppPlatformEntryPointManifest(["desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.DesktopWindow")])
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.platform.coverage.missing");
    }

    [Fact]
    public void Validate_rejects_two_entry_points_covering_the_same_platform()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["desktop"],
                [
                    new AppPlatformEntryPointManifest(["desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.DesktopWindow"),
                    new AppPlatformEntryPointManifest(["desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.OtherDesktopWindow")
                ])
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.platform.entryPoint.platform.duplicate");
    }

    [Fact]
    public void Validate_rejects_an_entry_point_referencing_a_platform_outside_supported()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["desktop"],
                [new AppPlatformEntryPointManifest(["desktop", "mobile"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.SharedWindow")])
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.platform.entryPoint.platform.unsupported");
    }

    [Fact]
    public void Validate_rejects_a_malformed_platform_identifier()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["Desktop"],
                [new AppPlatformEntryPointManifest(["Desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.DesktopWindow")])
        };

        ManifestValidationResult result = AppManifestValidator.Validate(manifest);

        Assert.Contains(result.Errors, error => error.Code == "manifest.platform.id.invalid");
    }

    [Fact]
    public void Resolve_treats_legacy_entryPoint_as_desktop_only()
    {
        AppManifest manifest = CreateBaseManifest();

        AppManifestPlatformResolution? resolution = AppManifestPlatformSupport.Resolve(manifest);

        Assert.NotNull(resolution);
        Assert.Equal([WellKnownAppPlatforms.Desktop], resolution!.SupportedPlatforms);
        Assert.Equal(manifest.EntryPoint, resolution.EntryPointsByPlatform[WellKnownAppPlatforms.Desktop]);
    }

    [Fact]
    public void Resolve_maps_each_supported_platform_to_its_entry_point()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                ["desktop", "mobile"],
                [
                    new AppPlatformEntryPointManifest(["desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.DesktopWindow"),
                    new AppPlatformEntryPointManifest(["mobile"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.MobileWindow")
                ])
        };

        AppManifestPlatformResolution? resolution = AppManifestPlatformSupport.Resolve(manifest);

        Assert.NotNull(resolution);
        Assert.Equal("HackerOs.Samples.PlatformApp.DesktopWindow", resolution!.EntryPointsByPlatform[WellKnownAppPlatforms.Desktop].Type);
        Assert.Equal("HackerOs.Samples.PlatformApp.MobileWindow", resolution.EntryPointsByPlatform[WellKnownAppPlatforms.Mobile].Type);
    }

    [Fact]
    public void Resolve_returns_null_when_declaration_is_ambiguous()
    {
        AppManifest manifest = CreateBaseManifest() with
        {
            Platform = new AppManifestPlatform(
                ["desktop"],
                [new AppPlatformEntryPointManifest(["desktop"], "HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.DesktopWindow")])
        };

        Assert.Null(AppManifestPlatformSupport.Resolve(manifest));
    }

    private static AppManifest CreateBaseManifest() => new()
    {
        Id = "org.hackeros.samples.platform-app",
        Name = "Platform Demo",
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "Demonstrates a manifest with multiple platform entry points.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("HackerOs.Samples.PlatformApp.dll", "HackerOs.Samples.PlatformApp.PlatformDemoWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };
}
