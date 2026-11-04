using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>Aggregation rules of ADR 0047 §3, on manifests written to a temporary directory.</summary>
public sealed class BuiltInApplicationsAggregatorTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("hackeros-aggregate-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteManifest(Type definition, string fileName)
    {
        GeneratedManifest generated = Assert.Single(
            AppDefinitionInspector.Inspect(typeof(ValidCommandApp).Assembly, [definition], KazoOsSdk.Version).Manifests);
        string path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, generated.CanonicalJson);
        return path;
    }

    [Fact]
    public void IsSystemApp_is_carried_per_reference()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
        [
            new(WriteManifest(typeof(ValidCommandApp), "valid.json"), IsSystemApp: true),
            new(WriteManifest(typeof(SecondCommandApp), "second.json"), IsSystemApp: false)
        ]);

        Assert.Empty(result.Diagnostics);
        IReadOnlyList<BuiltInApplication> applications = BuiltInApplicationsJsonSerializer.DeserializeStrict(result.Json!);
        Assert.Equal(
            [("org.hackeros.tests.second", false), ("org.hackeros.tests.valid", true)],
            applications.Select(item => (item.Manifest.Id, item.IsSystemApp)));
    }

    [Fact]
    public void Duplicate_id_across_projects_fails_with_HOSAPP0021()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
        [
            new(WriteManifest(typeof(ValidCommandApp), "one.json"), false),
            new(WriteManifest(typeof(DuplicateIdApp), "two.json"), false)
        ]);

        Assert.Null(result.Json);
        GeneratorDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GeneratorCodes.InvalidCatalog, diagnostic.Code);
        Assert.Contains("catalog.app-id.duplicate", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_command_name_across_apps_fails_with_HOSAPP0022()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
        [
            new(WriteManifest(typeof(ValidCommandApp), "valid.json"), false),
            new(WriteManifest(typeof(SameCommandApp), "same.json"), false)
        ]);

        Assert.Null(result.Json);
        GeneratorDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GeneratorCodes.DuplicateCommand, diagnostic.Code);
        Assert.Equal("org.hackeros.tests.valid", diagnostic.Subject);
        Assert.Contains("org.hackeros.tests.same-command", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_negative_oom_score_adj_is_refused_for_a_non_system_app_with_HOSAPP0024()
    {
        string manifest = WriteManifest(typeof(ShieldedApp), "shielded.json");

        AggregationResult refused = BuiltInApplicationsAggregator.Aggregate([new(manifest, IsSystemApp: false)]);
        GeneratorDiagnostic diagnostic = Assert.Single(refused.Diagnostics);
        Assert.Equal("HOSAPP0024", diagnostic.Code);
        Assert.Equal("org.hackeros.tests.shielded", diagnostic.Subject);
        Assert.Contains("oomScoreAdj -300", diagnostic.Message, StringComparison.Ordinal);
        Assert.Null(refused.Json);

        AggregationResult accepted = BuiltInApplicationsAggregator.Aggregate([new(manifest, IsSystemApp: true)]);
        Assert.Empty(accepted.Diagnostics);
        Assert.Contains("\"oomScoreAdj\":-300", accepted.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_system_app_may_register_the_file_view_and_the_file_dialogs()
    {
        // ADR 0156 §1: both contracts are open to any window app; HOSAPP0025 is retired.
        AppManifest manifest = new()
        {
            Id = "org.example.files",
            Name = "Files",
            Version = "1.0.0",
            PublisherId = "org.example",
            Description = "Registers the file view.",
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("Example.Files", "Example.Files.Window"),
            SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
            Presentation = new PresentationManifest("files", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None,
            Components =
            [
                new AppComponentManifest(AppComponentContracts.FileView, "Example.Files", "Example.Files.View"),
                new AppComponentManifest(AppComponentContracts.FileDialogs, "Example.Files", "Example.Files.Dialogs")
            ]
        };

        string path = Path.Combine(_directory, "files.json");
        File.WriteAllText(path, AppManifestJsonSerializer.SerializeCanonical(manifest));

        AggregationResult result = BuiltInApplicationsAggregator.Aggregate([new(path, IsSystemApp: false)]);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(AppComponentContracts.FileDialogs, result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public void Command_named_like_a_shell_builtin_fails_with_HOSAPP0022()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
            [new(WriteManifest(typeof(BuiltinNameApp), "builtin.json"), false)]);

        GeneratorDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GeneratorCodes.DuplicateCommand, diagnostic.Code);
        Assert.Contains("'eval'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void App_built_for_a_newer_sdk_fails_the_host_build_with_HOSAPP0021()
    {
        GeneratedManifest generated = Assert.Single(
            AppDefinitionInspector.Inspect(typeof(ValidCommandApp).Assembly, [typeof(ValidCommandApp)], "0.99.0").Manifests);
        string path = Path.Combine(_directory, "newer.json");
        File.WriteAllText(path, generated.CanonicalJson);

        AggregationResult result = BuiltInApplicationsAggregator.Aggregate([new(path, false)]);

        GeneratorDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(GeneratorCodes.InvalidCatalog, diagnostic.Code);
        Assert.Contains("catalog.sdk.incompatible", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_or_malformed_manifest_fails_with_HOSAPP0020()
    {
        string malformed = Path.Combine(_directory, "bad.json");
        File.WriteAllText(malformed, """{"schemaVersion":1,"unknown":true}""");

        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
        [
            new(Path.Combine(_directory, "missing.json"), false),
            new(malformed, false)
        ]);

        Assert.Null(result.Json);
        Assert.Equal([GeneratorCodes.UnreadableManifest, GeneratorCodes.UnreadableManifest], result.Diagnostics.Select(item => item.Code));
    }

    [Fact]
    public void No_app_gives_an_empty_document()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate([]);

        Assert.Equal("{\"schemaVersion\":1,\"applications\":[]}\n", result.Json);
    }

    [Fact]
    public void List_parsing_defaults_to_non_system_and_merges_duplicates()
    {
        string a = Path.Combine(_directory, "a.json");
        string b = Path.Combine(_directory, "b.json");

        IReadOnlyList<AggregationInput> inputs = BuiltInApplicationsAggregator.ParseList(
            [$"|{b}", $"true|{a}", $"false|{a}", "", $"False|{b}"]);

        Assert.Equal([new AggregationInput(a, true), new AggregationInput(b, false)], inputs);
    }

    [Fact]
    public void List_lines_may_carry_the_assembly_and_dependency_list_of_the_image()
    {
        string a = Path.Combine(_directory, "a.json");
        string dll = Path.Combine(_directory, "A.dll");
        string deps = Path.Combine(_directory, "deps.txt");

        IReadOnlyList<AggregationInput> inputs = BuiltInApplicationsAggregator.ParseList([$"true|{a}|{dll}|{deps}", $"|{a}||"]);

        Assert.Equal([new AggregationInput(a, true, dll, deps)], inputs);
    }

    [Fact]
    public void Aggregation_sizes_each_app_image_from_its_built_assembly()
    {
        // ADR 0068: this very generator assembly stands in for an app: its own file is VmExe, and
        // Kazo.Os.App.Abstractions, which it references and finds in the dependency list, is shared.
        string assembly = typeof(BuiltInApplicationsAggregator).Assembly.Location;
        string abstractions = typeof(AppManifest).Assembly.Location;
        string deps = Path.Combine(_directory, "deps.txt");
        File.WriteAllLines(deps, [abstractions, typeof(object).Assembly.Location]);

        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
            [new(WriteManifest(typeof(ValidCommandApp), "valid.json"), false, assembly, deps)]);

        AppImage image = BuiltInApplicationsJsonSerializer.DeserializeStrict(result.Json!)[0].Image!;
        Assert.Equal(new FileInfo(assembly).Length, image.OwnBytes);
        AppImageAssembly shared = Assert.Single(image.Assemblies, static item => item.Shared && item.Name == "Kazo.Os.App.Abstractions.dll");
        Assert.Equal(new FileInfo(abstractions).Length, shared.Bytes);
        // The runtime (System.Private.CoreLib) is the OS's own, never part of an app image.
        Assert.DoesNotContain(image.Assemblies, static item => item.Name.StartsWith("System.", StringComparison.Ordinal));
    }

    [Fact]
    public void An_app_without_a_known_assembly_has_no_image()
    {
        AggregationResult result = BuiltInApplicationsAggregator.Aggregate(
            [new(WriteManifest(typeof(ValidCommandApp), "valid.json"), false)]);

        Assert.Null(BuiltInApplicationsJsonSerializer.DeserializeStrict(result.Json!)[0].Image);
    }

    [Theory]
    [InlineData("System.Text.Json", true)]
    [InlineData("Microsoft.AspNetCore.Components", true)]
    [InlineData("netstandard", true)]
    [InlineData("Kazo.Os.AppSdk", false)]
    [InlineData("MudBlazor", false)]
    public void Runtime_and_framework_assemblies_are_not_part_of_an_image(string name, bool framework) =>
        Assert.Equal(framework, AppImageInspector.IsFramework(name));
}
