using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary>Writing a <c>.hackapp</c> package and its <c>index.json</c> entry (ADR 0182 §1, §2).</summary>
public sealed class AppPackageWriterTests
{
    [Fact]
    public void A_written_package_reads_back_with_its_assembly_and_assets()
    {
        Dictionary<string, byte[]> sample = AppPackageTests.SampleFiles();

        byte[] package = AppPackageWriter.Write(
            sample["manifest.json"],
            assembly => sample["lib/" + assembly],
            asset => sample["wwwroot/" + asset]);
        AppPackage read = AppPackage.Read(package);

        Assert.Equal((AppPackageTests.AppId, "1.0.0"), (read.AppId, read.Version.ToString()));
        Assert.Equal(["hackeros-package.json", "lib/Kazo.Sample.dll", "manifest.json", "wwwroot/data/x.txt"], read.Files.Keys.Order(StringComparer.Ordinal));
        // Same inputs, same bytes: the SHA-256 in index.json is reproducible.
        Assert.Equal(AppPackage.Sha256(package), AppPackage.Sha256(AppPackageWriter.Write(
            sample["manifest.json"], assembly => sample["lib/" + assembly], asset => sample["wwwroot/" + asset])));
    }

    [Fact]
    public void The_satellites_of_the_app_go_to_lib_culture_and_are_listed_with_their_sha256()
    {
        Dictionary<string, byte[]> sample = AppPackageTests.SampleFiles();
        byte[] french = [5, 6, 7];

        byte[] package = AppPackageWriter.Write(
            sample["manifest.json"],
            assembly => sample["lib/" + assembly],
            asset => sample["wwwroot/" + asset],
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["fr/Kazo.Sample.resources.dll"] = french });
        AppPackage read = AppPackage.Read(package);

        Assert.Equal(french, read.Files["lib/fr/Kazo.Sample.resources.dll"]);
        Assert.Equal(["fr/Kazo.Sample.resources.dll"], read.SatelliteAssemblies.Keys);
    }

    [Fact]
    public void A_satellite_of_another_assembly_is_refused_like_the_os_would()
    {
        Dictionary<string, byte[]> sample = AppPackageTests.SampleFiles();

        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageWriter.Write(
            sample["manifest.json"],
            assembly => sample["lib/" + assembly],
            asset => sample["wwwroot/" + asset],
            new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["fr/HackerOs.AppSdk.resources.dll"] = [1] }));

        Assert.Equal("apps.package.unexpected-file", refused.Code);
    }

    [Fact]
    public void A_changed_asset_is_refused_like_the_os_would()
    {
        Dictionary<string, byte[]> sample = AppPackageTests.SampleFiles();

        AppPackageException refused = Assert.Throws<AppPackageException>(() => AppPackageWriter.Write(
            sample["manifest.json"], assembly => sample["lib/" + assembly], _ => Encoding.UTF8.GetBytes("edited")));

        Assert.Equal("apps.package.hash-mismatch", refused.Code);
    }

    [Fact]
    public void Index_entries_are_added_replaced_and_kept_newest_first()
    {
        string sha = new('a', 64);
        AppPackageIndexEntry first = new(SemanticVersion.Parse("1.0.0"), AppPackageWriter.GetPackagePath(AppPackageTests.AppId, SemanticVersion.Parse("1.0.0")), sha);
        AppPackageIndexEntry second = new(SemanticVersion.Parse("1.1.0"), "1.1.0/org.kazo.sample-1.1.0.hackapp", sha);
        AppPackageIndexEntry republished = first with { Sha256 = new string('b', 64) };

        byte[] index = AppPackageWriter.AddToIndex(null, AppPackageTests.AppId, first);
        index = AppPackageWriter.AddToIndex(index, AppPackageTests.AppId, second);
        index = AppPackageWriter.AddToIndex(index, AppPackageTests.AppId, republished);
        AppPackageIndex parsed = AppPackageIndex.Parse(index);

        Assert.Equal("1.0.0/org.kazo.sample-1.0.0.hackapp", first.Path);
        Assert.Equal(["1.1.0", "1.0.0"], parsed.Versions.Select(static entry => entry.Version.ToString()));
        Assert.Equal(new string('b', 64), parsed.Find(SemanticVersion.Parse("1.0.0"))!.Sha256);
        Assert.Equal("apps.index.invalid", Assert.Throws<AppPackageException>(() => AppPackageWriter.AddToIndex(index, "org.other", first)).Code);
    }

    [Fact]
    public void Signatures_already_in_the_index_survive_a_new_version_and_a_republished_one_takes_its_own()
    {
        string sha = new('a', 64);
        byte[] firstSignature = [.. Enumerable.Repeat((byte)1, AppPackageSignature.SignatureLength)];
        AppPackageIndexEntry first = new(SemanticVersion.Parse("1.0.0"), "1.0.0/org.kazo.sample-1.0.0.hackapp", sha) { Signature = firstSignature };
        AppPackageIndexEntry second = new(SemanticVersion.Parse("1.1.0"), "1.1.0/org.kazo.sample-1.1.0.hackapp", sha);

        byte[] index = AppPackageWriter.AddToIndex(null, AppPackageTests.AppId, first);
        index = AppPackageWriter.AddToIndex(index, AppPackageTests.AppId, second);
        AppPackageIndex kept = AppPackageIndex.Parse(index);
        index = AppPackageWriter.AddToIndex(index, AppPackageTests.AppId, first with { Signature = null });
        AppPackageIndex republished = AppPackageIndex.Parse(index);

        Assert.Equal(firstSignature, kept.Find(SemanticVersion.Parse("1.0.0"))!.Signature);
        Assert.Null(kept.Find(SemanticVersion.Parse("1.1.0"))!.Signature);
        Assert.Null(republished.Find(SemanticVersion.Parse("1.0.0"))!.Signature);
    }
}
