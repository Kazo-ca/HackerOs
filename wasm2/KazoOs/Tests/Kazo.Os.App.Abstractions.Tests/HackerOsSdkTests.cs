using System.Reflection;
using System.Xml.Linq;

namespace Kazo.Os.App.Abstractions.Tests;

/// <summary>ADR 0098 §1 and §3: the SDK version of the process and the support policy.</summary>
public sealed class HackerOsSdkTests
{
    [Fact]
    public void Version_is_the_number_of_build_Versions_props()
    {
        string props = Path.Combine(RepositoryRoot(), "build", "Versions.props");
        string expected = XDocument.Load(props).Descendants("HackerOsVersion").Single().Value.Trim();

        Assert.Equal(expected, HackerOsSdk.Version);
        Assert.Equal(SemanticVersion.Parse(expected), HackerOsSdk.SemanticVersion);
    }

    [Theory]
    [InlineData("0.9.0-preview.1+4f2c1a", "0.9.0-preview.1")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("not a version", "4.5.6")]
    [InlineData(null, "4.5.6")]
    public void NormalizeVersion_drops_build_metadata_and_falls_back(string? informational, string expected) =>
        Assert.Equal(expected, HackerOsSdk.NormalizeVersion(informational, new Version(4, 5, 6, 7)));

    [Fact]
    public void ReadVersion_reads_the_informational_version_of_an_assembly() =>
        Assert.Equal(HackerOsSdk.Version, HackerOsSdk.ReadVersion(typeof(AppManifest).Assembly));

    [Theory]
    // Same major, minimum lower or equal: loads (prereleases compare by SemVer precedence).
    [InlineData("0.9.0-preview.1", null, "0.9.0-preview.1", true)]
    [InlineData("0.9.0-preview.1", null, "0.9.0-preview.3", true)]
    [InlineData("0.9.0-preview.1", null, "0.9.0", true)]
    [InlineData("1.0.0", null, "1.4.2", true)]
    // Compiled against a newer SDK than the OS: refused.
    [InlineData("0.9.0-preview.3", null, "0.9.0-preview.2", false)]
    [InlineData("0.9.0", null, "0.9.0-preview.9", false)]
    [InlineData("1.3.0", null, "1.2.9", false)]
    // Another major version: refused both ways.
    [InlineData("1.0.0", null, "2.0.0", false)]
    [InlineData("0.9.0-preview.1", null, "1.0.0", false)]
    // A declared maximum is honored.
    [InlineData("1.0.0", "1.2.0", "1.2.0", true)]
    [InlineData("1.0.0", "1.2.0", "1.3.0", false)]
    // Bounds that are not SemVer never match.
    [InlineData("one", null, "1.0.0", false)]
    public void IsCompatible_applies_the_same_major_policy(string minimum, string? maximum, string sdk, bool expected) =>
        Assert.Equal(expected, HackerOsSdk.IsCompatible(new AppSdkCompatibilityManifest(minimum, maximum), SemanticVersion.Parse(sdk)));

    [Fact]
    public void The_process_sdk_accepts_an_app_compiled_against_it() =>
        Assert.True(HackerOsSdk.IsCompatible(new AppSdkCompatibilityManifest(HackerOsSdk.Version)));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Kazo.Os.sln not found above the test output.");
    }
}
