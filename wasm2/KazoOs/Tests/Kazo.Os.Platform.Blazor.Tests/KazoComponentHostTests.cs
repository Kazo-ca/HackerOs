using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.Platform.Blazor.Components;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.ComponentProviders;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>The OS registry of embeddable components (ADR 0063).</summary>
public sealed class KazoComponentHostTests
{
    private const string Contract = "kazoos.tests-view";
    private static readonly string AssemblyName = typeof(TestEmbed).Assembly.GetName().Name + ".dll";

    [Fact]
    public async Task Resolves_the_registered_component_after_loading_its_app()
    {
        AppManifest provider = CreateManifest("org.kazoos.provider", typeof(TestEmbed));
        var loads = new List<string>();
        KazoComponentHost host = CreateHost([provider], loads: loads);

        KazoComponentResolution resolution = await host.ResolveAsync(Contract);

        Assert.True(resolution.IsResolved);
        Assert.Equal(typeof(TestEmbed), resolution.ComponentType);
        Assert.Equal("org.kazoos.provider", resolution.ProviderAppId);
        Assert.Equal(["org.kazoos.provider"], loads);
    }

    [Fact]
    public async Task Reports_when_no_installed_app_provides_the_contract()
    {
        AppManifest provider = CreateManifest("org.kazoos.provider", typeof(TestEmbed));
        KazoComponentHost host = CreateHost([provider], disabled: ["org.kazoos.provider"]);

        KazoComponentResolution resolution = await host.ResolveAsync(Contract);

        Assert.False(resolution.IsResolved);
        Assert.Contains("No installed app", resolution.Error);
    }

    [Fact]
    public async Task Rejects_a_type_that_is_not_an_embeddable_component()
    {
        AppManifest provider = CreateManifest("org.kazoos.provider", typeof(NotEmbeddable));
        KazoComponentHost host = CreateHost([provider]);

        KazoComponentResolution resolution = await host.ResolveAsync(Contract);

        Assert.False(resolution.IsResolved);
        Assert.Equal("org.kazoos.provider", resolution.ProviderAppId);
        Assert.Contains("not an embeddable component", resolution.Error);
    }

    [Fact]
    public async Task System_apps_are_tried_before_other_providers()
    {
        AppManifest thirdParty = CreateManifest("org.example.alpha", typeof(TestEmbed));
        AppManifest system = CreateManifest("org.kazoos.zeta", typeof(TestEmbed));
        KazoComponentHost host = CreateHost([thirdParty, system], systemAppIds: ["org.kazoos.zeta"]);

        KazoComponentResolution resolution = await host.ResolveAsync(Contract);

        Assert.Equal("org.kazoos.zeta", resolution.ProviderAppId);
    }

    [Fact]
    public async Task Falls_back_to_the_next_provider_when_one_does_not_load()
    {
        AppManifest broken = CreateManifest("org.kazoos.alpha", typeof(TestEmbed));
        AppManifest working = CreateManifest("org.kazoos.beta", typeof(TestEmbed));
        KazoComponentHost host = CreateHost([broken, working], unavailable: ["org.kazoos.alpha"]);

        KazoComponentResolution resolution = await host.ResolveAsync(Contract);

        Assert.Equal("org.kazoos.beta", resolution.ProviderAppId);
    }

    [Fact]
    public async Task A_platform_component_is_resolved_without_loading_any_app()
    {
        var loads = new List<string>();
        KazoComponentHost host = CreateHost(
            [],
            loads: loads,
            platform: [new KazoPlatformComponent(AppComponentContracts.ContextMenu, typeof(TestEmbed))]);

        KazoComponentResolution resolution = await host.ResolveAsync(AppComponentContracts.ContextMenu);

        Assert.True(resolution.IsResolved);
        Assert.Equal(typeof(TestEmbed), resolution.ComponentType);
        Assert.Equal(KazoComponentResolution.PlatformProviderId, resolution.ProviderAppId);
        Assert.Empty(loads);
    }

    [Fact]
    public void A_platform_component_must_be_embeddable()
    {
        Assert.Throws<ArgumentException>(() => new KazoPlatformComponent(AppComponentContracts.ContextMenu, typeof(NotEmbeddable)));
    }

    [Fact]
    public async Task Without_a_choice_the_system_app_provides_the_file_view_before_a_third_party_one()
    {
        // ADR 0156 §1-2: the file view is open to any window app, the explorer stays the default.
        AppManifest files = CreateManifest("org.example.files", typeof(TestEmbed), AppComponentContracts.FileView);
        AppManifest explorer = CreateManifest("org.kazoos.explorer", typeof(TestEmbed), AppComponentContracts.FileView);
        KazoComponentHost host = CreateHost([files, explorer], systemAppIds: ["org.kazoos.explorer"]);

        KazoComponentResolution resolution = await host.ResolveAsync(AppComponentContracts.FileView);

        Assert.Equal("org.kazoos.explorer", resolution.ProviderAppId);
        Assert.Equal(["org.kazoos.explorer", "org.example.files"], host.GetProviders(AppComponentContracts.FileView).Select(provider => provider.AppId));
    }

    [Fact]
    public async Task The_provider_an_administrator_chose_is_tried_first()
    {
        AppManifest files = CreateManifest("org.example.files", typeof(TestEmbed), AppComponentContracts.FileView);
        AppManifest explorer = CreateManifest("org.kazoos.explorer", typeof(TestEmbed), AppComponentContracts.FileView);
        var warnings = new List<string>();
        KazoComponentHost host = CreateHost(
            [files, explorer], systemAppIds: ["org.kazoos.explorer"], selection: new FakeSelection(AppComponentContracts.FileView, "org.example.files"), warnings: warnings);

        KazoComponentResolution resolution = await host.ResolveAsync(AppComponentContracts.FileView);

        Assert.Equal("org.example.files", resolution.ProviderAppId);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task A_chosen_provider_that_does_not_load_falls_back_to_the_explorer_with_one_warning()
    {
        AppManifest files = CreateManifest("org.example.files", typeof(TestEmbed), AppComponentContracts.FileView);
        AppManifest explorer = CreateManifest("org.kazoos.explorer", typeof(TestEmbed), AppComponentContracts.FileView);
        var warnings = new List<string>();
        var errors = new List<string>();
        KazoComponentHost host = CreateHost(
            [files, explorer],
            systemAppIds: ["org.kazoos.explorer"],
            unavailable: ["org.example.files"],
            errors: errors,
            selection: new FakeSelection(AppComponentContracts.FileView, "org.example.files"),
            warnings: warnings);

        KazoComponentResolution first = await host.ResolveAsync(AppComponentContracts.FileView);
        KazoComponentResolution second = await host.ResolveAsync(AppComponentContracts.FileView);

        Assert.Equal("org.kazoos.explorer", first.ProviderAppId);
        Assert.Equal("org.kazoos.explorer", second.ProviderAppId);
        // ADR 0156 §4: written once, as a warning; component.unavailable stays for "no provider at all".
        string warning = Assert.Single(warnings);
        Assert.StartsWith("component.fallback:", warning, StringComparison.Ordinal);
        Assert.Contains("org.example.files", warning, StringComparison.Ordinal);
        Assert.Contains("asset missing", warning, StringComparison.Ordinal);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task A_chosen_provider_disabled_for_the_user_falls_back_with_a_warning()
    {
        AppManifest files = CreateManifest("org.example.files", typeof(TestEmbed), AppComponentContracts.FileView);
        AppManifest explorer = CreateManifest("org.kazoos.explorer", typeof(TestEmbed), AppComponentContracts.FileView);
        var warnings = new List<string>();
        KazoComponentHost host = CreateHost(
            [files, explorer],
            disabled: ["org.example.files"],
            systemAppIds: ["org.kazoos.explorer"],
            selection: new FakeSelection(AppComponentContracts.FileView, "org.example.files"),
            warnings: warnings);

        KazoComponentResolution resolution = await host.ResolveAsync(AppComponentContracts.FileView);

        Assert.Equal("org.kazoos.explorer", resolution.ProviderAppId);
        Assert.Contains("disabled or uninstalled", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_choice_never_applies_to_an_open_contract()
    {
        AppManifest alpha = CreateManifest("org.kazoos.alpha", typeof(TestEmbed));
        AppManifest beta = CreateManifest("org.kazoos.beta", typeof(TestEmbed));
        KazoComponentHost host = CreateHost([alpha, beta], selection: new FakeSelection(Contract, "org.kazoos.beta"));

        KazoComponentResolution resolution = await host.ResolveAsync(Contract);

        Assert.Equal("org.kazoos.alpha", resolution.ProviderAppId);
    }

    [Fact]
    public async Task No_working_file_view_at_all_is_written_as_an_error()
    {
        AppManifest files = CreateManifest("org.example.files", typeof(NotEmbeddable), AppComponentContracts.FileView);
        var errors = new List<string>();
        KazoComponentHost host = CreateHost([files], errors: errors);

        KazoComponentResolution resolution = await host.ResolveAsync(AppComponentContracts.FileView);

        Assert.False(resolution.IsResolved);
        Assert.Contains("component.unavailable", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reserved_contract_is_given_to_a_system_app()
    {
        AppManifest explorer = CreateManifest("org.kazoos.explorer", typeof(TestEmbed), AppComponentContracts.FileView);
        var errors = new List<string>();
        KazoComponentHost host = CreateHost([explorer], systemAppIds: ["org.kazoos.explorer"], errors: errors);

        KazoComponentResolution resolution = await host.ResolveAsync(AppComponentContracts.FileView);

        Assert.Equal("org.kazoos.explorer", resolution.ProviderAppId);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task An_open_contract_without_provider_is_not_written_to_the_system_log()
    {
        var errors = new List<string>();
        KazoComponentHost host = CreateHost([], errors: errors);

        KazoComponentResolution resolution = await host.ResolveAsync(Contract);

        Assert.False(resolution.IsResolved);
        Assert.Empty(errors);
    }

    private static KazoComponentHost CreateHost(
        IReadOnlyList<AppManifest> manifests,
        IReadOnlyList<string>? disabled = null,
        IReadOnlyList<string>? systemAppIds = null,
        IReadOnlyList<string>? unavailable = null,
        List<string>? loads = null,
        IReadOnlyList<KazoPlatformComponent>? platform = null,
        List<string>? errors = null,
        IComponentProviderSelection? selection = null,
        List<string>? warnings = null)
    {
        AppCatalog catalog = Assert.IsType<AppCatalog>(AppCatalog.Build(manifests, systemAppIds ?? []).Catalog);
        var enablement = new AppEnablementRegistry(catalog);
        enablement.MarkDisabled(disabled ?? []);
        return new KazoComponentHost(catalog, enablement, (appId, _) =>
        {
            loads?.Add(appId);
            if (unavailable?.Contains(appId) == true)
            {
                return Task.FromResult<(AppDescriptor?, string?)>((null, "asset missing"));
            }

            AppManifest manifest = catalog.Manifests[appId];
            Assembly assembly = typeof(TestEmbed).Assembly;
            return Task.FromResult<(AppDescriptor?, string?)>((new AppDescriptor(manifest, typeof(TestWindow), assembly), null));
        }, platform, errors is null ? null : errors.Add)
        {
            Selection = selection,
            ReportWarning = warnings is null ? null : warnings.Add
        };
    }

    /// <summary>A components.config with one line.</summary>
    private sealed class FakeSelection(string contract, string appId) : IComponentProviderSelection
    {
        public ValueTask<string?> GetSelectedProviderAsync(string requested, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(requested == contract ? appId : null);
    }

    private static AppManifest CreateManifest(string id, Type componentType, string contract = Contract) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "pub.kazoos",
        Description = "Provides a test component.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest(AssemblyName, typeof(TestWindow).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Components = [new AppComponentManifest(contract, AssemblyName, componentType.FullName!)]
    };

    public sealed class TestWindow : WindowAppBase;

    public sealed class TestEmbed : KazoEmbeddableComponent;

    public sealed class NotEmbeddable;
}
