using System.Reflection;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>ADR 0151 §2 and ADR 0159 §2–§4: the generator runs <c>[RegisterFileContextMenu]</c> and writes <c>contextMenus</c>.</summary>
public sealed class ContextMenuRegistrationTests : IDisposable
{
    private static readonly Assembly Fixtures = typeof(MenuApp).Assembly;
    private readonly string _project = Directory.CreateTempSubdirectory("hackeros-menu-").FullName;

    public void Dispose() => Directory.Delete(_project, recursive: true);

    [Fact]
    public void The_registration_becomes_the_manifests_entries_with_their_titles_by_culture()
    {
        WriteResources("AppManifest.resx", ("Extract here", "Extract here"), ("Tools", "Tools"), ("Hash", "Hash"));
        WriteResources("AppManifest.fr.resx", ("Extract here", "Extraire ici"));

        InspectionResult result = AppDefinitionInspector.Inspect(Fixtures, [typeof(MenuApp)], "1.0.0", projectDirectory: _project);

        Assert.True(result.IsSuccess, string.Join(", ", result.Diagnostics.Select(static item => $"{item.Code} {item.Message}")));
        IReadOnlyList<AppContextMenuEntryManifest> entries = Assert.Single(result.Manifests).Manifest.ContextMenus!;
        Assert.Equal(["extract", "tools", "hash"], entries.Select(static entry => entry.Name));
        Assert.Equal([new AppContextMenuTitleManifest("en", "Extract here"), new AppContextMenuTitleManifest("fr", "Extraire ici")], entries[0].Titles);
        Assert.Equal(["file", "directory"], entries[2].Scopes);
        Assert.Contains("\"contextMenus\"", Assert.Single(result.Manifests).CanonicalJson, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_missing_from_the_english_resources_fails_the_build()
    {
        WriteResources("AppManifest.resx", ("Extract here", "Extract here"));

        InspectionResult result = AppDefinitionInspector.Inspect(Fixtures, [typeof(MenuApp)], "1.0.0", projectDirectory: _project);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static item => item.Code == GeneratorCodes.InvalidContextMenu && item.Message.Contains("'Tools'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(MenuWithoutCapabilityApp), GeneratorCodes.ContextMenuCapability)]
    [InlineData(typeof(BrokenMenuApp), GeneratorCodes.InvalidContextMenu)]
    [InlineData(typeof(ThrowingMenuApp), GeneratorCodes.InvalidContextMenu)]
    [InlineData(typeof(UnstableMenuApp), GeneratorCodes.InvalidContextMenu)]
    [InlineData(typeof(WrongSignatureMenuApp), GeneratorCodes.InvalidContextMenu)]
    public void A_bad_registration_has_its_code(Type definition, string code)
    {
        InspectionResult result = AppDefinitionInspector.Inspect(Fixtures, [definition], "1.0.0");

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, item => item.Code == code && item.Subject == definition.FullName);
    }

    [Fact]
    public void The_host_refuses_an_entry_targeting_an_app_it_lacks_or_an_undeclared_intent()
    {
        AppContextMenuEntryManifest entry = new()
        {
            Name = "view",
            Title = "View",
            Titles = [new("en", "View")],
            Target = "org.hackeros.tests.viewer",
            Intent = "org.hackeros.tests.viewer.view.v1"
        };
        AppManifest owner = Manifest("org.hackeros.tests.owner") with { ContextMenus = [entry] };
        AppManifest viewer = Manifest("org.hackeros.tests.viewer");

        Assert.Equal(GeneratorCodes.ContextMenuTarget, Assert.Single(BuiltInApplicationsAggregator.FindInvalidContextMenuTargets([owner])).Code);
        Assert.Equal(GeneratorCodes.ContextMenuTarget, Assert.Single(BuiltInApplicationsAggregator.FindInvalidContextMenuTargets([owner, viewer])).Code);
        Assert.Empty(BuiltInApplicationsAggregator.FindInvalidContextMenuTargets(
            [owner, viewer with { Intents = [new AppIntentDeclarationManifest("org.hackeros.tests.viewer.view.v1")] }]));
    }

    private void WriteResources(string file, params (string Key, string Value)[] values)
    {
        Directory.CreateDirectory(Path.Combine(_project, "Resources"));
        string data = string.Concat(values.Select(static item =>
            $"<data name=\"{item.Key}\" xml:space=\"preserve\"><value>{item.Value}</value></data>"));
        File.WriteAllText(Path.Combine(_project, "Resources", file), $"<?xml version=\"1.0\" encoding=\"utf-8\"?><root>{data}</root>");
    }

    private static AppManifest Manifest(string id) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "pub.local",
        Description = id,
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Tests.dll", "Tests.Window"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.ShellContextMenu]
    };
}
