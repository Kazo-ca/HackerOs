using System.Xml.Linq;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>The trimmer roots a host's aggregation writes (ADR 0106 §2, ADR 0113).</summary>
public sealed class TrimmerRootDescriptorWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("hackeros-trimmer-roots-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private AppManifest Manifest(Type definition) => Assert.Single(
        AppDefinitionInspector.Inspect(typeof(ValidCommandApp).Assembly, [definition], KazoOsSdk.Version).Manifests).Manifest;

    private string WriteManifest(Type definition, string fileName)
    {
        GeneratedManifest generated = Assert.Single(
            AppDefinitionInspector.Inspect(typeof(ValidCommandApp).Assembly, [definition], KazoOsSdk.Version).Manifests);
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, generated.CanonicalJson);
        return path;
    }

    private static IReadOnlyList<(string Name, string? Preserve)> Assemblies(string xml) =>
        [.. XDocument.Parse(xml).Root!.Elements("assembly")
            .Select(element => ((string)element.Attribute("fullname")!, (string?)element.Attribute("preserve")))];

    [Fact]
    public void Aggregation_roots_each_app_assembly_whole_once()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
        [
            new(WriteManifest(typeof(ValidCommandApp), "valid.json"), IsSystemApp: true),
            new(WriteManifest(typeof(SecondCommandApp), "second.json"), IsSystemApp: false)
        ]);

        Assert.Empty(result.Diagnostics);
        string assembly = Path.GetFileNameWithoutExtension(Manifest(typeof(ValidCommandApp)).EntryPoint!.Assembly);
        Assert.Equal([(assembly, "all")], Assemblies(result.TrimmerRoots!));
    }

    [Fact]
    public void Components_and_site_roots_add_their_assemblies_sorted()
    {
        AppManifest manifest = Manifest(typeof(ValidCommandApp)) with
        {
            Components = [new AppComponentManifest("tests.contract", "Zeta.Components.dll", "Zeta.Components.Panel")],
            Web = new WebAppManifest(["http://tests.hack/"], new WebBlazorManifest("Alpha.Site.dll", "Alpha.Site.App")),
        };

        string xml = TrimmerRootDescriptorWriter.Build([manifest]);

        string entryAssembly = Path.GetFileNameWithoutExtension(manifest.EntryPoint!.Assembly);
        Assert.Equal(
            new[] { "Alpha.Site", entryAssembly, "Zeta.Components" }.Order(StringComparer.Ordinal),
            Assemblies(xml).Select(item => item.Name));
        Assert.All(Assemblies(xml), item => Assert.Equal("all", item.Preserve));
        Assert.Equal(xml, TrimmerRootDescriptorWriter.Build([manifest]));
    }

    [Fact]
    public void A_failed_aggregation_writes_no_roots()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
        [
            new(WriteManifest(typeof(ValidCommandApp), "one.json"), false),
            new(WriteManifest(typeof(DuplicateIdApp), "two.json"), false)
        ]);

        Assert.Null(result.TrimmerRoots);
    }
}
