using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary>Reading and verifying a <c>.hackapp</c> package and its index (ADR 0182 §1, §2, §5).</summary>
public sealed class AppPackageTests
{
    internal const string AppId = "org.kazo.sample";

    internal static string ManifestJson(string version = "1.0.0", string assetSha = "") =>
        "{\"schemaVersion\":1,\"id\":\"" + AppId + "\",\"name\":\"Sample\",\"version\":\"" + version + "\",\"publisherId\":\"pub.kazo\","
        + "\"description\":\"A sample.\",\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],"
        + "\"assembly\":\"Kazo.Sample.dll\",\"type\":\"Kazo.Sample.SampleWindow\"}]},\"sdkCompatibility\":{\"minimumVersion\":\"2.0.0\"},"
        + "\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"visible\",\"iconAssetPaths\":[]},\"localizations\":[],\"capabilities\":[],"
        + "\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
        + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],"
        + "\"assets\":[" + (assetSha.Length == 0 ? string.Empty : "{\"path\":\"data/x.txt\",\"kind\":\"data\",\"sha256\":\"" + assetSha + "\"}") + "],"
        + "\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}";

    internal static Dictionary<string, byte[]> SampleFiles(string version = "1.0.0")
    {
        byte[] asset = Encoding.UTF8.GetBytes("hello");
        return new(StringComparer.Ordinal)
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(ManifestJson(version, AppPackage.Sha256(asset))),
            ["lib/Kazo.Sample.dll"] = [1, 2, 3],
            ["wwwroot/data/x.txt"] = asset,
        };
    }

    internal static byte[] Build(Dictionary<string, byte[]> files, string appId = AppId, string version = "1.0.0", Dictionary<string, string>? listed = null)
    {
        listed ??= files.ToDictionary(static pair => pair.Key, static pair => AppPackage.Sha256(pair.Value), StringComparer.Ordinal);
        string description = JsonSerializer.Serialize(new { appId, version, files = listed });
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, byte[] bytes) in files.Append(new("hackeros-package.json", Encoding.UTF8.GetBytes(description))))
            {
                using Stream entry = zip.CreateEntry(path).Open();
                entry.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    [Fact]
    public void A_complete_package_reads_with_its_manifest_assemblies_and_assets()
    {
        AppPackage package = AppPackage.Read(Build(SampleFiles()));

        Assert.Equal(AppId, package.AppId);
        Assert.Equal(SemanticVersion.Parse("1.0.0"), package.Version);
        Assert.Equal(["Kazo.Sample.dll"], package.Assemblies.Keys);
        Assert.Equal("hello", Encoding.UTF8.GetString(package.Files["wwwroot/data/x.txt"]));
    }

    [Fact]
    public void An_altered_file_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        Dictionary<string, string> listed = files.ToDictionary(static pair => pair.Key, static pair => AppPackage.Sha256(pair.Value));
        files["lib/Kazo.Sample.dll"] = [9, 9, 9];

        Assert.Equal("apps.package.hash-mismatch", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files, listed: listed))).Code);
    }

    [Fact]
    public void An_unlisted_file_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        Dictionary<string, string> listed = files.ToDictionary(static pair => pair.Key, static pair => AppPackage.Sha256(pair.Value));
        files["lib/Extra.dll"] = [4];

        Assert.Equal("apps.package.unlisted-file", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files, listed: listed))).Code);
    }

    [Fact]
    public void A_missing_entry_point_assembly_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files.Remove("lib/Kazo.Sample.dll");

        Assert.Equal("apps.package.missing-assembly", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Fact]
    public void A_file_outside_the_known_folders_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files["run.sh"] = [1];

        Assert.Equal("apps.package.unexpected-file", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Fact]
    public void A_package_that_names_another_app_or_version_is_refused()
    {
        Assert.Equal("apps.package.id-mismatch", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(SampleFiles(), appId: "org.kazo.other"))).Code);
        Assert.Equal("apps.package.version-mismatch", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(SampleFiles(), version: "2.0.0"))).Code);
    }

    [Fact]
    public void A_path_that_climbs_out_of_the_package_is_refused()
    {
        Dictionary<string, byte[]> files = SampleFiles();
        files["wwwroot/../../etc/passwd"] = [1];

        Assert.Equal("apps.package.unsafe-path", Assert.Throws<AppPackageException>(() => AppPackage.Read(Build(files))).Code);
    }

    [Fact]
    public void Garbage_is_refused_as_corrupt()
    {
        Assert.Equal("apps.package.corrupt", Assert.Throws<AppPackageException>(() => AppPackage.Read([1, 2, 3, 4])).Code);
    }

    [Fact]
    public void The_index_lists_versions_newest_first_and_prefers_stable_ones()
    {
        string sha = new('a', 64);
        AppPackageIndex index = AppPackageIndex.Parse(Encoding.UTF8.GetBytes(
            "{\"appId\":\"org.kazo.sample\",\"versions\":["
            + "{\"version\":\"1.0.0\",\"path\":\"1.0.0/s.hackapp\",\"sha256\":\"" + sha + "\"},"
            + "{\"version\":\"2.0.0-beta\",\"path\":\"2.0.0-beta/s.hackapp\",\"sha256\":\"" + sha + "\"},"
            + "{\"version\":\"1.1.0\",\"path\":\"1.1.0/s.hackapp\",\"sha256\":\"" + sha + "\"}]}"));

        Assert.Equal(["2.0.0-beta", "1.1.0", "1.0.0"], index.Versions.Select(static entry => entry.Version.ToString()));
        Assert.Equal("1.1.0", index.Latest!.Version.ToString());
        Assert.Equal("1.0.0/s.hackapp", index.Find(SemanticVersion.Parse("1.0.0"))!.Path);
        Assert.Null(index.Find(SemanticVersion.Parse("9.0.0")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"appId\":\"x\",\"versions\":[{\"version\":\"1.0.0\",\"path\":\"../x.hackapp\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}")]
    [InlineData("{\"appId\":\"x\",\"versions\":[{\"version\":\"1.0.0\",\"path\":\"x.zip\",\"sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}")]
    [InlineData("{\"appId\":\"x\",\"versions\":[{\"version\":\"1.0.0\",\"path\":\"x.hackapp\",\"sha256\":\"short\"}]}")]
    [InlineData("not json")]
    public void An_invalid_index_is_refused(string json)
    {
        Assert.Equal("apps.index.invalid", Assert.Throws<AppPackageException>(() => AppPackageIndex.Parse(Encoding.UTF8.GetBytes(json))).Code);
    }
}
