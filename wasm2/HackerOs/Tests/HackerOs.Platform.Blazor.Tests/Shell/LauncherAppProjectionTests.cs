using HackerOs.App.Abstractions;
using HackerOs.Platform.Blazor.Shell;
using HackerOs.Platform.Core.Lifecycle;

namespace HackerOs.Platform.Blazor.Tests.Shell;

/// <summary>Verifies the Start menu's catalog, category, search, and ordered-pin projection.</summary>
public sealed class LauncherAppProjectionTests
{
    [Fact]
    public void Create_derives_dynamic_categories_and_sorts_all_programs_by_name()
    {
        AppManifest terminal = CreateManifest("org.hackeros.terminal", "Terminal", "system");
        AppManifest files = CreateManifest("org.hackeros.files", "File Explorer", "utilities");

        LauncherAppProjectionResult result = LauncherAppProjection.Create(
            [terminal, files],
            new TestEnablementRegistry(),
            []);

        Assert.Equal(["system", "utilities"], result.Categories);
        Assert.Equal([files.Id, terminal.Id], result.VisibleApps.Select(app => app.Id));
    }

    [Fact]
    public void Create_filters_by_the_manifest_presentation_category()
    {
        AppManifest terminal = CreateManifest("org.hackeros.terminal", "Terminal", "system");
        AppManifest files = CreateManifest("org.hackeros.files", "File Explorer", "utilities");

        LauncherAppProjectionResult result = LauncherAppProjection.Create(
            [terminal, files],
            new TestEnablementRegistry(),
            [],
            selectedCategory: "UTILITIES");

        AppManifest projected = Assert.Single(result.VisibleApps);
        Assert.Equal(files.Id, projected.Id);
    }

    [Fact]
    public void Create_excludes_hidden_disabled_non_window_and_non_desktop_apps()
    {
        AppManifest eligible = CreateManifest("org.hackeros.eligible", "Eligible", "system");
        AppManifest hidden = CreateManifest("org.hackeros.hidden", "Hidden", "system") with
        {
            Presentation = new PresentationManifest("system", AppLaunchVisibility.Hidden, [])
        };
        AppManifest disabled = CreateManifest("org.hackeros.disabled", "Disabled", "system");
        AppManifest terminal = CreateManifest("org.hackeros.command", "Command", "system") with
        {
            Kind = AppKind.Terminal
        };
        AppManifest mobile = CreateMobileManifest("org.hackeros.mobile", "Mobile only", "system");

        LauncherAppProjectionResult result = LauncherAppProjection.Create(
            [eligible, hidden, disabled, terminal, mobile],
            new TestEnablementRegistry(disabled.Id),
            []);

        AppManifest projected = Assert.Single(result.LaunchableApps);
        Assert.Equal(eligible.Id, projected.Id);
    }

    [Theory]
    [InlineData("canvas", "org.hackeros.paint")]
    [InlineData("raster", "org.hackeros.paint")]
    [InlineData("hackeros.paint", "org.hackeros.paint")]
    [InlineData("graphics", "org.hackeros.paint")]
    public void Create_searches_name_description_id_and_category_globally(string query, string expectedAppId)
    {
        AppManifest paint = CreateManifest(
            "org.hackeros.paint",
            "Canvas",
            "graphics",
            "A raster image editor.");
        AppManifest settings = CreateManifest(
            "org.hackeros.settings",
            "Settings",
            "system",
            "Configure Kazo OS.");

        LauncherAppProjectionResult result = LauncherAppProjection.Create(
            [paint, settings],
            new TestEnablementRegistry(),
            [],
            selectedCategory: "system",
            searchQuery: query);

        AppManifest match = Assert.Single(result.VisibleApps);
        Assert.Equal(expectedAppId, match.Id);
    }

    [Fact]
    public void Create_preserves_persisted_pin_order_while_masking_unavailable_ids()
    {
        AppManifest alpha = CreateManifest("org.hackeros.alpha", "Alpha", "utilities");
        AppManifest beta = CreateManifest("org.hackeros.beta", "Beta", "utilities");
        AppManifest disabled = CreateManifest("org.hackeros.disabled", "Disabled", "utilities");
        AppManifest mobile = CreateMobileManifest("org.hackeros.mobile", "Mobile", "utilities");

        LauncherAppProjectionResult result = LauncherAppProjection.Create(
            [alpha, beta, disabled, mobile],
            new TestEnablementRegistry(disabled.Id),
            ["org.hackeros.missing", beta.Id, disabled.Id, mobile.Id, alpha.Id, beta.Id]);

        Assert.Equal([beta.Id, alpha.Id], result.PinnedApps.Select(app => app.Id));
    }

    private static AppManifest CreateManifest(
        string id,
        string name,
        string category,
        string description = "Desktop test application.") => new()
    {
        Id = id,
        Name = name,
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = description,
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("HackerOs.TestApp", "HackerOs.TestApp.TestWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest(category, AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None
    };

    private static AppManifest CreateMobileManifest(string id, string name, string category) =>
        CreateManifest(id, name, category) with
        {
            EntryPoint = null,
            Platform = new AppManifestPlatform(
                [WellKnownAppPlatforms.Mobile.Value],
                [
                    new AppPlatformEntryPointManifest(
                        [WellKnownAppPlatforms.Mobile.Value],
                        "HackerOs.TestApp",
                        "HackerOs.TestApp.TestMobileWindow")
                ])
        };

    private sealed class TestEnablementRegistry(params string[] disabledAppIds) : IAppEnablementRegistry
    {
        private readonly HashSet<string> _disabled = new(disabledAppIds, StringComparer.Ordinal);

        public bool IsEnabled(string appId) => !_disabled.Contains(appId);

        public IReadOnlyCollection<string> DisabledAppIds => _disabled;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }
}
