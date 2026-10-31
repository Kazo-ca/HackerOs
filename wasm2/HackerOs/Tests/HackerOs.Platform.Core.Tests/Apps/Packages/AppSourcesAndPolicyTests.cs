using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary>
/// The catalogs and trusted sources of <c>app-sources.config</c>, the block list of
/// <c>app-policy.config</c> and their build-time defaults (ADR 0189 §1, §2 and §5).
/// </summary>
public sealed class AppSourcesAndPolicyTests
{
    [Fact]
    public void The_default_file_lists_the_official_catalog_kazo_ca_and_the_update_interval()
    {
        AppSourcesConfig config = AppSourcesConfigDocument.Read(AppSourcesConfigDocument.DefaultContent);

        Assert.Empty(config.Warnings);
        Assert.Equal(new Uri(AppPackageDefaults.OfficialCatalogAddress), config.Catalogs["official"]);
        Assert.Equal(AppSourceTrust.Verified, config.GetCatalogTrust("official"));
        Assert.Equal(AppSourceTrust.Trusted, config.GetTrust(new AppPackageSource("Kazo-ca", "Anything")));
        Assert.Equal(AppPackageDefaults.DefaultUpdateIntervalHours, config.UpdateIntervalHours);
        Assert.Contains("github.com/Kazo-ca", config.BuiltInKeys);
    }

    [Fact]
    public void A_repository_can_be_trusted_without_its_owner()
    {
        AppSourcesConfig config = AppSourcesConfigDocument.Read("[trusted]\ngithub.com/someone/app=true\n");

        Assert.Equal(AppSourceTrust.Trusted, config.GetTrust(new AppPackageSource("someone", "App")));
        Assert.Equal(AppSourceTrust.Untrusted, config.GetTrust(new AppPackageSource("someone", "Other")));
    }

    [Theory]
    [InlineData("[updates]\ninterval-hours=0\n")]
    [InlineData("[updates]\ninterval-hours=721\n")]
    [InlineData("[catalogs]\nmirror=http://example.org/catalog.json\n")]
    [InlineData("[catalogs]\nmirror=https://user:pw@example.org/catalog.json\n")]
    [InlineData("[trusted]\nexample.org/x=true\n")]
    public void An_invalid_line_is_ignored_with_a_warning(string content)
    {
        AppSourcesConfig config = AppSourcesConfigDocument.Read(content);

        Assert.Single(config.Warnings);
        Assert.Equal(AppPackageDefaults.DefaultUpdateIntervalHours, config.UpdateIntervalHours);
    }

    [Fact]
    public void Adding_and_removing_sources_edits_the_right_sections()
    {
        Assert.True(AppSourceSpecification.TryParse("mirror=https://example.org/catalog.json", out AppSourceSpecification? catalog));
        Assert.True(AppSourceSpecification.TryParse("github:someone", out AppSourceSpecification? owner));

        string content = AppSourcesConfigDocument.AddSource(AppSourcesConfigDocument.DefaultContent, catalog, trusted: true);
        content = AppSourcesConfigDocument.AddSource(content, owner, trusted: false);
        AppSourcesConfig added = AppSourcesConfigDocument.Read(content);

        Assert.Equal(new Uri("https://example.org/catalog.json"), added.Catalogs["mirror"]);
        Assert.Equal(AppSourceTrust.Trusted, added.GetCatalogTrust("mirror"));
        Assert.Contains("github.com/someone", added.UntrustedKeys);
        Assert.Equal(AppSourceTrust.Untrusted, added.GetTrust(new AppPackageSource("someone", "x")));

        content = AppSourcesConfigDocument.RemoveSource(AppSourcesConfigDocument.RemoveSource(content, catalog), owner);
        AppSourcesConfig removed = AppSourcesConfigDocument.Read(content);
        Assert.False(removed.Catalogs.ContainsKey("mirror"));
        Assert.DoesNotContain("github.com/someone", removed.UntrustedKeys);
        Assert.Contains("official", removed.Catalogs.Keys);
    }

    [Theory]
    [InlineData("github:someone", "github.com/someone", "github.com/someone", false)]
    [InlineData("https://github.com/someone/app", "github.com/someone/app", "github.com/someone/app", false)]
    [InlineData("someone/app", "github.com/someone/app", "github.com/someone/app", false)]
    [InlineData("catalog:official", "official", "catalog:official", true)]
    [InlineData("mirror=https://example.org/c.json", "mirror", "catalog:mirror", true)]
    public void A_source_is_parsed_in_every_accepted_form(string text, string name, string trustKey, bool isCatalog)
    {
        Assert.True(AppSourceSpecification.TryParse(text, out AppSourceSpecification? source));
        Assert.Equal((name, trustKey, isCatalog), (source.Name, source.TrustKey, source.IsCatalog));
    }

    [Theory]
    [InlineData("")]
    [InlineData("someone")]
    [InlineData("http://example.org/catalog.json")]
    [InlineData("not a source")]
    public void A_bad_source_is_refused(string text)
    {
        Assert.False(AppSourceSpecification.TryParse(text, out _));
    }

    [Fact]
    public void The_block_list_matches_an_app_its_category_or_a_capability()
    {
        string content = AppPolicyConfigDocument.Set(AppPolicyConfigDocument.DefaultContent, AppBlockKind.Category, "Utilities", blocked: true);
        AppPolicy policy = AppPolicyConfigDocument.Read(content);
        AppManifest manifest = Manifest("org.kazo.sample", "utilities", "network.http");

        Assert.Equal(new AppBlockReason(AppBlockKind.Category, "utilities"), policy.FindBlock(manifest));
        Assert.Equal("blocked-categories: utilities", policy.FindBlock(manifest)!.ToString());
        Assert.Null(AppPolicyConfigDocument.Read(AppPolicyConfigDocument.Set(content, AppBlockKind.Category, "Utilities", blocked: false))
            .FindBlock(manifest));

        AppPolicy byCapability = AppPolicyConfigDocument.Read("[blocked-capabilities]\nnetwork.http=true\n");
        Assert.Equal(AppBlockKind.Capability, byCapability.FindBlock(manifest)!.Kind);
        AppPolicy byApp = AppPolicyConfigDocument.Read("[blocked-apps]\norg.kazo.sample=true\norg.kazo.other=maybe\n");
        Assert.Equal(AppBlockKind.App, byApp.FindBlock(manifest)!.Kind);
        Assert.Single(byApp.Warnings);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("x=y")]
    [InlineData("[x]")]
    public void A_block_key_that_would_break_the_file_is_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => AppPolicyConfigDocument.Set(null, AppBlockKind.App, value, blocked: true));
    }

    [Fact]
    public void Build_time_defaults_replace_only_what_the_host_sets()
    {
        AppPackageDefaults defaults = AppPackageDefaults.FromMetadata(new Dictionary<string, string>
        {
            ["Catalogs"] = "school=https://school.example.org/catalog.json, bad",
            ["UpdateIntervalHours"] = "24",
            ["BlockedCategories"] = "social; games-free",
        });

        Assert.Equal("school", Assert.Single(defaults.Catalogs).Key);
        Assert.Equal(AppPackageDefaults.Standard.TrustedKeys, defaults.TrustedKeys);
        Assert.Equal(24, defaults.UpdateIntervalHours);
        Assert.Equal(["social", "games-free"], defaults.BlockedCategories);

        AppSourcesConfig sources = AppSourcesConfigDocument.Read(AppSourcesConfigDocument.BuildDefaultContent(defaults), defaults);
        Assert.Equal(24, sources.UpdateIntervalHours);
        Assert.True(sources.Catalogs.ContainsKey("school"));
        AppPolicy policy = AppPolicyConfigDocument.Read(AppPolicyConfigDocument.BuildDefaultContent(defaults), defaults);
        Assert.Contains("social", policy.BlockedCategories);
    }

    [Fact]
    public void An_interval_out_of_range_keeps_the_standard_value()
    {
        Assert.Equal(
            AppPackageDefaults.DefaultUpdateIntervalHours,
            AppPackageDefaults.FromMetadata(new Dictionary<string, string> { ["UpdateIntervalHours"] = "100000" }).UpdateIntervalHours);
    }

    private static AppManifest Manifest(string id, string category, params string[] capabilities) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "org.test",
        Description = id,
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Test", $"{id}.EntryPoint"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest(category, AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = capabilities,
    };
}
