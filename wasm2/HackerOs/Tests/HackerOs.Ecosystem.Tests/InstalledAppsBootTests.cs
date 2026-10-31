using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Ecosystem.Tests;

/// <summary>Installed apps join the host's catalog at boot (ADR 0182 §5, Q5 answer C), then without a restart (ADR 0214 §1).</summary>
public sealed class InstalledAppsBootTests
{
    private static AppManifest Manifest(string id, string minimumSdk = "2.0.0", string version = "1.0.0") => AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(
        "{\"schemaVersion\":1,\"id\":\"" + id + "\",\"name\":\"Sample\",\"version\":\"" + version + "\",\"publisherId\":\"pub.kazo\","
        + "\"description\":\"A sample.\",\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],"
        + "\"assembly\":\"Kazo.Sample.dll\",\"type\":\"Kazo.Sample.SampleWindow\"}]},\"sdkCompatibility\":{\"minimumVersion\":\"" + minimumSdk + "\"},"
        + "\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"visible\",\"iconAssetPaths\":[]},\"localizations\":[],\"capabilities\":[],"
        + "\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
        + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],"
        + "\"assets\":[],\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}"));

    private static InstalledApp App(string id, string minimumSdk = "2.0.0", string version = "1.0.0") =>
        new(new AppPackageSource("Kazo-ca", "Sample", SemanticVersion.Parse(version)), Manifest(id, minimumSdk, version));

    private static InstalledAppsBoot Boot(params InstalledApp[] apps)
    {
        InstalledAppsBoot boot = new(BuildKnownLazyApps.Host);
        boot.Apply(new InstalledAppsImage(apps, []), (_, _) => ValueTask.FromResult<byte[]?>(null));
        return boot;
    }

    [Fact]
    public void An_app_installed_after_the_boot_joins_the_live_catalog_at_once()
    {
        InstalledAppsBoot boot = Boot();
        AppCatalog live = boot.Live;
        int changes = 0;
        live.Changed += (_, _) => changes++;

        InstalledAppsActivation activation = boot.Refresh(new InstalledAppsImage([App("org.kazo.sample")], []), static _ => false);

        Assert.Equal(["org.kazo.sample"], activation.Added);
        Assert.Empty(activation.WaitingForRestart);
        Assert.Same(live, boot.Live);
        Assert.True(live.Manifests.ContainsKey("org.kazo.sample"));
        Assert.Equal(1, changes);
        Assert.True(boot.Packages.Contains("org.kazo.sample"));
        Assert.False(BuildKnownLazyApps.Catalog.Manifests.ContainsKey("org.kazo.sample"));
    }

    [Fact]
    public void An_update_of_an_app_never_loaded_takes_effect_and_one_already_loaded_waits()
    {
        InstalledAppsBoot boot = Boot(App("org.kazo.sample"), App("org.kazo.other"));

        InstalledAppsActivation activation = boot.Refresh(
            new InstalledAppsImage([App("org.kazo.sample", version: "1.1.0"), App("org.kazo.other", version: "2.0.0")], []),
            app => app.AppId == "org.kazo.other");

        Assert.Equal(["org.kazo.sample"], activation.Updated);
        Assert.Equal(["org.kazo.other"], activation.WaitingForRestart);
        Assert.Equal("1.1.0", boot.Live.Manifests["org.kazo.sample"].Version);
        Assert.Equal("1.0.0", boot.Live.Manifests["org.kazo.other"].Version);
    }

    [Fact]
    public void A_removed_app_leaves_the_live_catalog_unless_it_is_loaded()
    {
        InstalledAppsBoot boot = Boot(App("org.kazo.sample"), App("org.kazo.other"));

        InstalledAppsActivation activation = boot.Refresh(InstalledAppsImage.Empty, app => app.AppId == "org.kazo.other");

        Assert.Equal(["org.kazo.sample"], activation.Removed);
        Assert.Equal(["org.kazo.other"], activation.WaitingForRestart);
        Assert.False(boot.Live.Manifests.ContainsKey("org.kazo.sample"));
        Assert.True(boot.Live.Manifests.ContainsKey("org.kazo.other"));
    }

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
