using HackerOs.App.Abstractions;
using Xunit;

namespace HackerOs.Apps.Store.Tests;

public sealed class StoreManifestTests
{
    [Fact]
    public void Manifest_is_the_store_window_on_desktop_and_mobile()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new StoreApp());

        Assert.Equal("org.hackeros.store", manifest.Id);
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.Equal("Hack Store", manifest.Name);
        Assert.True(manifest.SingleInstancePerUser);
        Assert.Contains("desktop", manifest.Platform!.Supported);
        Assert.Contains("mobile", manifest.Platform!.Supported);
    }

    [Fact]
    public void Manifest_asks_only_to_manage_and_launch_apps()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new StoreApp());

        Assert.Equal([AppCapabilities.AppsLaunch, AppCapabilities.AppsManage], manifest.Capabilities.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Manifest_passes_validation()
    {
        ManifestValidationResult result = AppManifestValidator.Validate(AppDefinitionMapper.ToManifest(new StoreApp()));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }
}
