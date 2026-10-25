using HackerOs.App.Abstractions;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Discovery;

namespace HackerOs.Samples.PlatformApp.Tests;

/// <summary>
/// End-to-end proof for <c>MOB-014</c> (partial): one manifest with a single, shared entry point
/// declared for both <c>desktop</c> and <c>mobile</c> (doc/en/wasm/dev/old/mobile-interface-platform-plan.md §4.1's
/// first example), validated and resolved through the real <see cref="AppManifestValidator"/>,
/// <see cref="AppCatalog"/>, and <see cref="AppEntryPointDiscovery"/> pipeline.
/// </summary>
public sealed class PlatformDemoWindowTests
{
    [Fact]
    public void Definition_declares_platform_not_legacy_entryPoint()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PlatformSampleApp());

        Assert.Null(manifest.EntryPoint);
        Assert.NotNull(manifest.Platform);
    }

    [Fact]
    public void Definition_manifest_is_valid()
    {
        ManifestValidationResult result = AppManifestValidator.Validate(AppDefinitionMapper.ToManifest(new PlatformSampleApp()));

        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));
    }

    [Fact]
    public void Resolve_finds_the_same_shared_entry_point_for_desktop_and_mobile()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PlatformSampleApp());

        AppPlatformEntryPointResolution desktop = AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Desktop);
        AppPlatformEntryPointResolution mobile = AppPlatformEntryPointResolver.Instance.Resolve(manifest, WellKnownAppPlatforms.Mobile);

        Assert.True(desktop.IsResolved);
        Assert.True(mobile.IsResolved);
        Assert.Equal(desktop.EntryPoint, mobile.EntryPoint);
        Assert.Equal("HackerOs.Samples.PlatformApp.PlatformDemoWindow", desktop.EntryPoint!.Type);
    }

    [Fact]
    public void Discovery_resolves_the_component_type_on_both_platforms()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PlatformSampleApp());
        AppCatalogBuildResult catalogResult = AppCatalog.Build([manifest]);
        Assert.True(catalogResult.IsSuccess, string.Join(", ", catalogResult.Errors.Select(e => e.Message)));

        Dictionary<string, System.Reflection.Assembly> hostAssemblies = new(StringComparer.Ordinal)
        {
            ["HackerOs.Samples.PlatformApp.dll"] = typeof(PlatformDemoWindow).Assembly
        };

        AppDiscoveryResult desktopDiscovery = AppEntryPointDiscovery.Discover(catalogResult.Catalog!, hostAssemblies, WellKnownAppPlatforms.Desktop);
        AppDiscoveryResult mobileDiscovery = AppEntryPointDiscovery.Discover(catalogResult.Catalog!, hostAssemblies, WellKnownAppPlatforms.Mobile);

        Assert.True(desktopDiscovery.IsSuccess);
        Assert.True(mobileDiscovery.IsSuccess);
        Assert.Equal(typeof(PlatformDemoWindow), desktopDiscovery.Descriptors![manifest.Id].EntryPointType);
        Assert.Equal(typeof(PlatformDemoWindow), mobileDiscovery.Descriptors![manifest.Id].EntryPointType);
    }

    [Fact]
    public void Generated_manifest_json_matches_the_definition_platform_declaration()
    {
        string manifestPath = FindCheckedInManifestPath();
        AppManifest fromDisk = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(manifestPath));

        // Omitted optional collections deserialize as null; normalize before validating.
        fromDisk = AppManifestJsonSerializer.NormalizeOptionalCollections(fromDisk);

        ManifestValidationResult result = AppManifestValidator.Validate(fromDisk);
        Assert.True(result.IsValid, string.Join(", ", result.Errors.Select(e => $"{e.Code}: {e.Message}")));

        Assert.Equal(AppDefinitionMapper.ToManifest(new PlatformSampleApp()).Id, fromDisk.Id);
        Assert.Null(fromDisk.EntryPoint);
        Assert.NotNull(fromDisk.Platform);
        Assert.Equal(
            [.. AppDefinitionMapper.ToManifest(new PlatformSampleApp()).Platform!.Supported.OrderBy(v => v, StringComparer.Ordinal)],
            fromDisk.Platform!.Supported.OrderBy(v => v, StringComparer.Ordinal).ToArray());
    }

    private static string FindCheckedInManifestPath()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "HackerOs.sln");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory, "Apps", "Samples", "HackerOs.Samples.PlatformApp", "manifest.json");
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not locate HackerOs.sln by walking up from the test output directory.");
    }
}
