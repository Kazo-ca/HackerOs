using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Ecosystem.Tests;

/// <summary>Installed apps join the host's catalog at boot (ADR 0182 §5, Q5 answer C).</summary>
public sealed class InstalledAppsBootTests
{
    private static AppManifest Manifest(string id, string minimumSdk = "2.0.0") => AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(
        "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"name\":\"Sample\",\"version\":\"1.0.0\",\"publisherId\":\"pub.kazo\","
        + "\"description\":\"A sample.\",\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],"
        + "\"assembly\":\"Kazo.Sample.dll\",\"type\":\"Kazo.Sample.SampleWindow\"}]},\"sdkCompatibility\":{\"minimumVersion\":\"" + minimumSdk + "\"},"
        + "\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"visible\",\"iconAssetPaths\":[]},\"localizations\":[],\"capabilities\":[],"
        + "\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
        + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],"
        + "\"assets\":[],\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}"));

    private static InstalledApp App(string id, string minimumSdk = "2.0.0") =>
        new(new AppPackageSource("Kazo-ca", "Sample", SemanticVersion.Parse("1.0.0")), Manifest(id, minimumSdk));

    [Fact]
    public void An_installed_app_joins_the_catalog_and_the_lazy_assemblies()
    {
        HostAppCatalog combined = BuildKnownLazyApps.Host.WithInstalledApps(new InstalledAppsImage([App("org.kazo.sample")], []), out IReadOnlyList<string> warnings);

        Assert.Empty(warnings);
        Assert.True(combined.Catalog.Manifests.ContainsKey("org.kazo.sample"));
        Assert.False(combined.Catalog.IsSystemApp("org.kazo.sample"));
        Assert.Contains("Kazo.Sample.dll", combined.LazyAssemblyNames);
        Assert.Equal(BuildKnownLazyApps.Catalog.Manifests.Count + 1, combined.Catalog.Manifests.Count);
        Assert.Single(combined.Installed);
    }

    [Fact]
    public void An_installed_app_cannot_take_the_ID_of_a_built_in_app()
    {
        string builtIn = BuildKnownLazyApps.Catalog.Manifests.Keys.First();

        HostAppCatalog combined = BuildKnownLazyApps.Host.WithInstalledApps(new InstalledAppsImage([App(builtIn)], []), out IReadOnlyList<string> warnings);

        Assert.Same(BuildKnownLazyApps.Host, combined);
        Assert.Contains("has the ID of an app built into this system", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void An_installed_app_for_another_SDK_is_left_out_and_the_others_still_join()
    {
        HostAppCatalog combined = BuildKnownLazyApps.Host.WithInstalledApps(
            new InstalledAppsImage([App("org.kazo.future", minimumSdk: "99.0.0"), App("org.kazo.sample")], []),
            out IReadOnlyList<string> warnings);

        Assert.False(combined.Catalog.Manifests.ContainsKey("org.kazo.future"));
        Assert.True(combined.Catalog.Manifests.ContainsKey("org.kazo.sample"));
        Assert.Contains(warnings, warning => warning.Contains("org.kazo.future cannot run here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assets_of_an_installed_app_come_from_its_folder()
    {
        InstalledAppsBoot boot = new(BuildKnownLazyApps.Host);
        boot.Apply(new InstalledAppsImage([App("org.kazo.sample")], []), (path, _) => ValueTask.FromResult<byte[]?>(
            path.Value == "/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/wwwroot/data/x.txt" ? Encoding.UTF8.GetBytes("hello") : null));
        InstalledAppAssetSource source = new(NoAppAssetSource.Instance, boot);

        byte[]? bytes = await source.ReadAsync(Manifest("org.kazo.sample"), "data/x.txt");

        Assert.Equal("hello", Encoding.UTF8.GetString(bytes!));
        Assert.Null(await source.ReadAsync(Manifest("org.kazo.sample"), "../../../etc/shadow"));
        Assert.Throws<InvalidOperationException>(() => boot.Apply(InstalledAppsImage.Empty, (_, _) => ValueTask.FromResult<byte[]?>(null)));
    }
}
