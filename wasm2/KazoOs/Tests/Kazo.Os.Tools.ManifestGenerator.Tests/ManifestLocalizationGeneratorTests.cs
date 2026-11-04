using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>
/// <c>Resources/AppManifest.&lt;culture&gt;.resx</c> → <c>localization/</c> files, manifest entries,
/// <c>HOSAPP0040</c> and the aggregated <c>localized</c> texts (ADR 0104, section 4).
/// </summary>
public sealed class ManifestLocalizationGeneratorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("kazoos-manifest-l10n-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static IReadOnlyList<GeneratedManifest> Generate(params Type[] types) =>
        AppDefinitionInspector.Inspect(typeof(ValidCommandApp).Assembly, types, KazoOsSdk.Version).Manifests;

    private void WriteResx(string culture, params (string Key, string Value)[] entries)
    {
        string resources = Path.Combine(_directory, "Resources");
        Directory.CreateDirectory(resources);
        string data = string.Concat(entries.Select(entry =>
            $"  <data name=\"{entry.Key}\" xml:space=\"preserve\"><value>{System.Security.SecurityElement.Escape(entry.Value)}</value></data>\n"));
        File.WriteAllText(
            Path.Combine(resources, $"AppManifest.{culture}.resx"),
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n{data}</root>\n");
    }

    [Fact]
    public void A_single_app_gets_localization_culture_json_and_a_manifest_entry()
    {
        WriteResx("fr", ("Name", "Valide"), ("Description", "Une commande « valide »"), ("Terminal.Usage", "valid [fichier]"));

        ManifestLocalizationGenerator.Result result = ManifestLocalizationGenerator.Apply(_directory, Generate(typeof(ValidCommandApp)));

        Assert.Empty(result.Diagnostics);
        string file = Assert.Single(result.Files);
        Assert.Equal(Path.Combine(_directory, "localization", "fr.json"), file);
        AppManifestText text = AppManifestTextJsonSerializer.DeserializeStrict(File.ReadAllText(file));
        Assert.Equal(new AppManifestText("Valide", "Une commande « valide »", "valid [fichier]"), text);
        LocalizationManifest entry = Assert.Single(Assert.Single(result.Manifests).Manifest.Localizations);
        Assert.Equal(("fr", "localization/fr.json"), (entry.Culture, entry.ResourcePath));
        Assert.Contains("\"localizations\"", Assert.Single(result.Manifests).CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_keys_keep_the_English_texts()
    {
        WriteResx("fr", ("Description", "Seulement la description"));

        ManifestLocalizationGenerator.Result result = ManifestLocalizationGenerator.Apply(_directory, Generate(typeof(ValidCommandApp)));

        AppManifest english = Assert.Single(Generate(typeof(ValidCommandApp))).Manifest;
        AppManifestText text = AppManifestTextJsonSerializer.DeserializeStrict(File.ReadAllText(Assert.Single(result.Files)));
        Assert.Equal(english.Name, text.Name);
        Assert.Equal("Seulement la description", text.Description);
        Assert.Equal(english.Terminal!.Usage, text.TerminalUsage);
    }

    [Fact]
    public void Several_apps_use_prefixed_keys_and_one_folder_per_app()
    {
        WriteResx("fr", ("org.kazoos.tests.second.Description", "Deuxième"), ("org.kazoos.tests.valid.Terminal.Usage", "valid [fichier]"));

        ManifestLocalizationGenerator.Result result = ManifestLocalizationGenerator.Apply(
            _directory, Generate(typeof(ValidCommandApp), typeof(SecondCommandApp)));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [
                Path.Combine(_directory, "localization", "org.kazoos.tests.second", "fr.json"),
                Path.Combine(_directory, "localization", "org.kazoos.tests.valid", "fr.json")
            ],
            result.Files);
        Assert.All(result.Manifests, generated => Assert.Equal(
            $"localization/{generated.Manifest.Id}/fr.json",
            Assert.Single(generated.Manifest.Localizations).ResourcePath));
    }

    [Theory]
    [InlineData("Title")]
    [InlineData("org.kazoos.tests.other.Name")]
    public void An_unknown_key_fails_with_HOSAPP0040_and_writes_nothing(string key)
    {
        WriteResx("fr", (key, "x"));

        ManifestLocalizationGenerator.Result result = ManifestLocalizationGenerator.Apply(_directory, Generate(typeof(ValidCommandApp)));

        GeneratorDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("HOSAPP0040", diagnostic.Code);
        Assert.Empty(result.Files);
        Assert.False(Directory.Exists(Path.Combine(_directory, "localization")));
    }

    [Fact]
    public void A_file_whose_resx_is_gone_is_deleted()
    {
        WriteResx("fr", ("Name", "Valide"));
        ManifestLocalizationGenerator.Apply(_directory, Generate(typeof(ValidCommandApp)));
        File.Delete(Path.Combine(_directory, "Resources", "AppManifest.fr.resx"));

        ManifestLocalizationGenerator.Result result = ManifestLocalizationGenerator.Apply(_directory, Generate(typeof(ValidCommandApp)));

        Assert.Empty(result.Files);
        Assert.False(File.Exists(Path.Combine(_directory, "localization", "fr.json")));
        Assert.Empty(Assert.Single(result.Manifests).Manifest.Localizations);
    }

    [Fact]
    public void The_aggregator_carries_the_translations_into_BuiltInApplications_json()
    {
        WriteResx("fr", ("Name", "Valide"), ("Description", "Une commande valide"));
        ManifestLocalizationGenerator.Result generated = ManifestLocalizationGenerator.Apply(_directory, Generate(typeof(ValidCommandApp)));
        string manifestPath = Path.Combine(_directory, "manifest.json");
        File.WriteAllText(manifestPath, Assert.Single(generated.Manifests).CanonicalJson);

        AggregationResult result = BuiltInApplicationsAggregator.Aggregate([new(manifestPath, IsSystemApp: true)]);

        Assert.Empty(result.Diagnostics);
        BuiltInApplication application = Assert.Single(BuiltInApplicationsJsonSerializer.DeserializeStrict(result.Json!));
        Assert.Equal("Valide", application.Localized!["fr"].Name);
    }
}
