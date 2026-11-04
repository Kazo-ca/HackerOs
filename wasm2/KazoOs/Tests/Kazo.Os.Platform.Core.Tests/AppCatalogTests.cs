using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;

namespace Kazo.Os.Platform.Core.Tests;

public sealed class AppCatalogTests
{
    [Fact]
    public void Build_orders_dependencies_deterministically_and_deactivation_in_reverse()
    {
        AppManifest shell = CreateManifest(
            "org.kazoos.shell",
            dependencies: [new AppDependencyManifest("org.kazoos.kernel", "1.0.0")]);
        AppManifest files = CreateManifest("org.kazoos.files");
        AppManifest kernel = CreateManifest("org.kazoos.kernel");

        AppCatalogBuildResult result = AppCatalog.Build([shell, files, kernel]);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["org.kazoos.files", "org.kazoos.kernel", "org.kazoos.shell"],
            result.Catalog!.ActivationOrder);
        Assert.Equal(result.Catalog.ActivationOrder.Reverse(), result.Catalog.DeactivationOrder);
    }

    [Fact]
    public void Build_rejects_missing_required_dependency()
    {
        AppManifest app = CreateManifest(
            "org.kazoos.editor",
            dependencies: [new AppDependencyManifest("org.kazoos.files", "1.0.0")]);

        AppCatalogBuildResult result = AppCatalog.Build([app]);

        Assert.Contains(result.Errors, error => error.Code == "catalog.dependency.missing");
    }

    [Fact]
    public void Build_allows_missing_optional_dependency()
    {
        AppManifest app = CreateManifest(
            "org.kazoos.editor",
            dependencies: [new AppDependencyManifest("org.kazoos.preview", "1.0.0", Optional: true)]);

        AppCatalogBuildResult result = AppCatalog.Build([app]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Build_rejects_incompatible_dependency_prerelease()
    {
        AppManifest dependency = CreateManifest("org.kazoos.files", "2.0.0-beta.1");
        AppManifest app = CreateManifest(
            "org.kazoos.editor",
            dependencies: [new AppDependencyManifest("org.kazoos.files", "2.0.0")]);

        AppCatalogBuildResult result = AppCatalog.Build([dependency, app]);

        Assert.Contains(result.Errors, error => error.Code == "catalog.dependency.incompatible");
    }

    [Fact]
    public void Build_rejects_dependency_cycles()
    {
        AppManifest first = CreateManifest(
            "org.kazoos.first",
            dependencies: [new AppDependencyManifest("org.kazoos.second", "1.0.0")]);
        AppManifest second = CreateManifest(
            "org.kazoos.second",
            dependencies: [new AppDependencyManifest("org.kazoos.first", "1.0.0")]);

        AppCatalogBuildResult result = AppCatalog.Build([first, second]);

        Assert.Contains(result.Errors, error => error.Code == "catalog.dependency.cycle");
    }

    [Fact]
    public void Build_rejects_duplicate_app_ids()
    {
        AppManifest first = CreateManifest("org.kazoos.files");
        AppManifest second = CreateManifest("org.kazoos.files");

        AppCatalogBuildResult result = AppCatalog.Build([first, second]);

        Assert.Contains(result.Errors, error => error.Code == "catalog.app-id.duplicate");
    }

    [Fact]
    public void Build_without_system_ids_has_no_system_app()
    {
        AppCatalog catalog = AppCatalog.Build([CreateManifest("org.kazoos.files")]).Catalog!;

        Assert.Empty(catalog.SystemAppIds);
        Assert.False(catalog.IsSystemApp("org.kazoos.files"));
    }

    [Fact]
    public void Build_exposes_host_declared_system_apps()
    {
        AppCatalogBuildResult result = AppCatalog.Build(
            [CreateManifest("org.kazoos.files"), CreateManifest("org.kazoos.paint")],
            ["org.kazoos.files", "org.kazoos.files"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["org.kazoos.files"], result.Catalog!.SystemAppIds);
        Assert.True(result.Catalog.IsSystemApp("org.kazoos.files"));
        Assert.False(result.Catalog.IsSystemApp("org.kazoos.paint"));
        // Ordinal: the flag is bound to the exact immutable id.
        Assert.False(result.Catalog.IsSystemApp("ORG.KAZOOS.FILES"));
        Assert.False(result.Catalog.IsSystemApp("org.kazoos.unknown"));
    }

    [Fact]
    public void Build_rejects_system_id_outside_the_catalog()
    {
        AppCatalogBuildResult result = AppCatalog.Build([CreateManifest("org.kazoos.files")], ["org.kazoos.termnial"]);

        Assert.False(result.IsSuccess);
        AppCatalogError error = Assert.Single(result.Errors);
        Assert.Equal("catalog.system-app.unknown", error.Code);
        Assert.Equal("org.kazoos.termnial", error.AppId);
    }

    [Fact]
    public void BuildFromBuiltInApplications_takes_the_system_flag_of_each_entry()
    {
        AppCatalogBuildResult result = AppCatalog.BuildFromBuiltInApplications(
        [
            new BuiltInApplication(IsSystemApp: true, CreateManifest("org.kazoos.terminal")),
            new BuiltInApplication(IsSystemApp: false, CreateManifest("org.kazoos.paint")),
        ]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["org.kazoos.terminal"], result.Catalog!.SystemAppIds);
        Assert.Equal(["org.kazoos.paint", "org.kazoos.terminal"], result.Catalog.ActivationOrder);
    }

    [Fact]
    public void Build_moves_an_app_built_for_another_sdk_to_rejected_without_failing()
    {
        AppManifest current = CreateManifest("org.kazoos.files") with { SdkCompatibility = new AppSdkCompatibilityManifest("1.0.0") };
        AppManifest newer = CreateManifest("org.kazoos.newer") with { SdkCompatibility = new AppSdkCompatibilityManifest("1.9.0") };
        AppManifest otherMajor = CreateManifest("org.kazoos.legacy") with { SdkCompatibility = new AppSdkCompatibilityManifest("0.9.0-preview.1") };

        AppCatalogBuildResult result = AppCatalog.Build([current, newer, otherMajor], [], SemanticVersion.Parse("1.2.0"));

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(["org.kazoos.files"], result.Catalog!.Manifests.Keys);
        Assert.Equal(["org.kazoos.legacy", "org.kazoos.newer"], result.Catalog.Rejected.Select(item => item.AppId));
        Assert.All(result.Catalog.Rejected, item => Assert.Equal("catalog.sdk.incompatible", item.Code));
        Assert.Contains("1.9.0", result.Catalog.Rejected[1].Message);
    }

    [Fact]
    public void Build_rejects_the_dependents_of_a_refused_app_and_keeps_optional_ones()
    {
        AppSdkCompatibilityManifest sdk1 = new("1.0.0");
        AppManifest newer = CreateManifest("org.kazoos.newer") with { SdkCompatibility = new AppSdkCompatibilityManifest("1.9.0") };
        AppManifest needs = CreateManifest("org.kazoos.needs", dependencies: [new AppDependencyManifest("org.kazoos.newer", "1.0.0")]) with { SdkCompatibility = sdk1 };
        AppManifest needsNeeds = CreateManifest("org.kazoos.needs-needs", dependencies: [new AppDependencyManifest("org.kazoos.needs", "1.0.0")]) with { SdkCompatibility = sdk1 };
        AppManifest optional = CreateManifest("org.kazoos.optional", dependencies: [new AppDependencyManifest("org.kazoos.newer", "1.0.0", Optional: true)]) with { SdkCompatibility = sdk1 };

        AppCatalogBuildResult result = AppCatalog.Build([newer, needs, needsNeeds, optional], ["org.kazoos.needs"], SemanticVersion.Parse("1.2.0"));

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors));
        Assert.Equal(["org.kazoos.optional"], result.Catalog!.Manifests.Keys);
        Assert.Equal(
            [("org.kazoos.needs", "catalog.dependency.unavailable"), ("org.kazoos.needs-needs", "catalog.dependency.unavailable"), ("org.kazoos.newer", "catalog.sdk.incompatible")],
            result.Catalog.Rejected.Select(item => (item.AppId, item.Code)));
        // The host's system flag is kept for display: Settings shows a refused system app as such.
        Assert.True(result.Catalog.IsSystemApp("org.kazoos.needs"));
    }

    [Fact]
    public void Build_gives_a_duplicate_command_to_the_system_app_then_to_the_first_activated()
    {
        AppManifest first = CreateCommand("org.kazoos.a-tool", "tool", ["t"]);
        AppManifest second = CreateCommand("org.kazoos.b-tool", "tool", ["bt"]);
        AppManifest system = CreateCommand("org.kazoos.z-tool", "t", []);

        AppCatalogBuildResult result = AppCatalog.Build([first, second, system], ["org.kazoos.z-tool"]);

        AppCatalog catalog = result.Catalog!;
        Assert.Equal("org.kazoos.a-tool", catalog.FindCommandOwner("TOOL"));
        Assert.Equal("org.kazoos.z-tool", catalog.FindCommandOwner("t"));
        Assert.Equal("org.kazoos.b-tool", catalog.FindCommandOwner("bt"));
        Assert.Equal(3, catalog.Manifests.Count);
        Assert.Equal(
            [("org.kazoos.a-tool", "catalog.command.duplicate"), ("org.kazoos.b-tool", "catalog.command.duplicate")],
            catalog.Warnings.Select(warning => (warning.AppId, warning.Code)));
    }

    [Fact]
    public void Build_registers_component_providers_system_apps_first()
    {
        AppManifest alpha = CreateProvider("org.example.alpha", "kazoos.terminal");
        AppManifest zeta = CreateProvider("org.kazoos.zeta", "kazoos.terminal");

        AppCatalog catalog = AppCatalog.Build([alpha, zeta], ["org.kazoos.zeta"]).Catalog!;

        Assert.Equal(["org.kazoos.zeta", "org.example.alpha"], catalog.GetComponentProviders("kazoos.terminal").Select(provider => provider.AppId));
        Assert.Empty(catalog.GetComponentProviders("kazoos.unknown"));
        Assert.Empty(catalog.Warnings);
    }

    [Fact]
    public void Build_registers_a_third_party_provider_of_the_file_view_after_the_system_app()
    {
        // ADR 0156 §1: the file view is open to any window app; the system app stays first.
        AppManifest explorer = CreateProvider("org.kazoos.explorer", AppComponentContracts.FileView);
        AppManifest files = CreateProvider("org.example.files", AppComponentContracts.FileView);

        AppCatalog catalog = AppCatalog.Build([explorer, files], ["org.kazoos.explorer"]).Catalog!;

        Assert.Equal(
            ["org.kazoos.explorer", "org.example.files"],
            catalog.GetComponentProviders(AppComponentContracts.FileView).Select(provider => provider.AppId));
        Assert.Empty(catalog.Warnings);
    }

    [Fact]
    public void Replacing_the_contents_keeps_the_instance_and_raises_changed()
    {
        AppCatalog live = AppCatalog.Build([CreateManifest("org.test.a")]).Catalog!.CreateLiveCopy();
        AppCatalog next = AppCatalog.Build([CreateManifest("org.test.a"), CreateManifest("org.test.b")]).Catalog!;
        int changes = 0;
        live.Changed += (_, _) => changes++;
        long revision = live.Revision;

        live.ReplaceContents(next);

        Assert.Equal(1, changes);
        Assert.Equal(revision + 1, live.Revision);
        Assert.Contains("org.test.b", live.Manifests.Keys);
        Assert.Equal(next.ActivationOrder.Count, live.ActivationOrder.Count);
    }

    [Fact]
    public void A_live_copy_does_not_change_the_catalog_it_was_copied_from()
    {
        AppCatalog builtIn = AppCatalog.Build([CreateManifest("org.test.a")]).Catalog!;
        AppCatalog live = builtIn.CreateLiveCopy();

        live.ReplaceContents(AppCatalog.Build([CreateManifest("org.test.a"), CreateManifest("org.test.b")]).Catalog!);

        Assert.DoesNotContain("org.test.b", builtIn.Manifests.Keys);
        Assert.NotSame(builtIn, live);
    }

    private static AppManifest CreateProvider(string appId, string contract) => CreateManifest(appId) with
    {
        Components = [new AppComponentManifest(contract, "Kazo.Os.TestApps", "TestApps.Embed")]
    };

    private static AppManifest CreateCommand(string appId, string name, IReadOnlyList<string> aliases) => CreateManifest(appId) with
    {
        Kind = AppKind.Terminal,
        Terminal = new TerminalCommandManifest(name, aliases, name)
    };

    private static AppManifest CreateManifest(
        string appId,
        string version = "1.0.0",
        IReadOnlyList<AppDependencyManifest>? dependencies = null) => new()
    {
        Id = appId,
        Name = appId,
        Version = version,
        PublisherId = "org.kazoos",
        Description = $"Test manifest for {appId}.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.TestApps", $"TestApps.{appId.Replace('.', '_')}"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Dependencies = dependencies ?? []
    };
}