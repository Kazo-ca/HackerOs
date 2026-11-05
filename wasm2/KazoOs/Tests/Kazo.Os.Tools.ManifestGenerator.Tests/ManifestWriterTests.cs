using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>File layout and write-only-when-changed behavior of <see cref="ManifestWriter"/>.</summary>
public sealed class ManifestWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("kazoos-manifest-writer-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static IReadOnlyList<GeneratedManifest> Generate(params Type[] types) =>
        AppDefinitionInspector.Inspect(typeof(ValidCommandApp).Assembly, types, "1.0.0").Manifests;

    [Fact]
    public void Single_app_writes_manifest_json_at_project_root()
    {
        IReadOnlyList<string> files = ManifestWriter.Write(_directory, Generate(typeof(ValidCommandApp)));

        string path = Assert.Single(files);
        Assert.Equal(Path.Combine(_directory, "manifest.json"), path);
        AppManifest roundTrip = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(path));
        Assert.Equal("org.kazoos.tests.valid", roundTrip.Id);
    }

    [Fact]
    public void Several_apps_write_one_file_per_app_and_remove_the_single_file()
    {
        ManifestWriter.Write(_directory, Generate(typeof(ValidCommandApp)));

        IReadOnlyList<string> files = ManifestWriter.Write(_directory, Generate(typeof(ValidCommandApp), typeof(SecondCommandApp)));

        Assert.Equal(
            [Path.Combine(_directory, "manifests", "org.kazoos.tests.second.json"), Path.Combine(_directory, "manifests", "org.kazoos.tests.valid.json")],
            files);
        Assert.False(File.Exists(Path.Combine(_directory, "manifest.json")));
    }

    [Fact]
    public void Going_back_to_one_app_removes_the_manifests_directory()
    {
        ManifestWriter.Write(_directory, Generate(typeof(ValidCommandApp), typeof(SecondCommandApp)));

        ManifestWriter.Write(_directory, Generate(typeof(ValidCommandApp)));

        Assert.False(Directory.Exists(Path.Combine(_directory, "manifests")));
        Assert.True(File.Exists(Path.Combine(_directory, "manifest.json")));
    }

    [Fact]
    public void Identical_content_is_not_rewritten_and_hand_edits_are_overwritten()
    {
        string path = Path.Combine(_directory, "manifest.json");
        Assert.True(ManifestWriter.WriteIfChanged(path, "{}\n"));
        DateTime written = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, written);

        Assert.False(ManifestWriter.WriteIfChanged(path, "{}\n"));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        File.WriteAllText(path, "{ \"edited\": true }");
        Assert.True(ManifestWriter.WriteIfChanged(path, "{}\n"));
        Assert.Equal("{}\n", File.ReadAllText(path));
    }
}
