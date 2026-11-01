using System.Reflection;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Ecosystem.Tests;

/// <summary>ADR 0222 §2: the OS reads an installed app's satellite assemblies before its code runs.</summary>
public sealed class InstalledAppSatellitesTests
{
    private const string AppId = "org.kazo.sample";

    private static InstalledApp App() => new(
        new AppPackageSource("Kazo-ca", "Sample", SemanticVersion.Parse("1.0.0")),
        AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(
            "{\"schemaVersion\":1,\"id\":\"" + AppId + "\",\"name\":\"Sample\",\"version\":\"1.0.0\",\"publisherId\":\"pub.kazo\","
            + "\"description\":\"A sample.\",\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],"
            + "\"assembly\":\"Kazo.Sample.dll\",\"type\":\"Kazo.Sample.SampleWindow\"}]},\"sdkCompatibility\":{\"minimumVersion\":\"2.0.0\"},"
            + "\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"visible\",\"iconAssetPaths\":[]},\"localizations\":[],\"capabilities\":[],"
            + "\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
            + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],"
            + "\"assets\":[],\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}")));

    [Fact]
    public void English_the_neutral_language_needs_no_satellite()
    {
        InstalledAppSatellites satellites = new(["en", "fr", "FR", "en-US", ""]);

        Assert.Equal(["fr"], satellites.Cultures);
    }

    [Fact]
    public async Task Each_language_is_read_from_lib_culture_and_a_missing_one_is_skipped()
    {
        List<string> asked = [];
        InstalledAppSatellites satellites = new(["fr", "de"]);

        IReadOnlyList<string> found = await satellites.LoadAsync(App(), "Kazo.Sample.dll", (path, _) =>
        {
            asked.Add(path.Value);
            return ValueTask.FromResult<byte[]?>(null);
        }, CancellationToken.None);

        Assert.Empty(found);
        Assert.Equal(
            [
                "/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/lib/fr/Kazo.Sample.resources.dll",
                "/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/lib/de/Kazo.Sample.resources.dll",
            ],
            asked);
    }

    [Fact]
    public void The_runtime_gets_no_answer_for_a_satellite_the_os_did_not_load()
    {
        Assert.Null(InstalledAppSatellites.Find(new AssemblyName("Kazo.Unknown.resources, Culture=fr")));
        Assert.Null(InstalledAppSatellites.Find(new AssemblyName("Kazo.Unknown")));
    }

    [Fact]
    public void A_personal_install_reads_its_satellites_from_the_users_copy()
    {
        InstalledApp app = App() with { FilesHome = "/home/alice" };

        Assert.Equal(
            "/home/alice/.local/share/hackeros/apps/org.kazo.sample/1.0.0/lib/fr/Kazo.Sample.resources.dll",
            app.GetSatellitePath("fr", "Kazo.Sample.dll").Value);
    }
}
