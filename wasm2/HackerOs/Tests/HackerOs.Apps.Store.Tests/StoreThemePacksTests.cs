using System.Globalization;
using HackerOs.AppSdk.Apps;
using Xunit;

namespace HackerOs.Apps.Store.Tests;

/// <summary>The « Themes » tab of the Store (ADR 0213 §5): theme packs of the catalogs merged with the installed ones.</summary>
public sealed class StoreThemePacksTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    private static HackCatalogApp Pack(string catalog, string id, string name, string latest = "1.1.0", params string[] tags) =>
        new(catalog, id, name, "themes", tags,
            new Dictionary<string, string> { ["en"] = "A dark pack", ["fr"] = "Un pack sombre" },
            "CC-BY-4.0", "https://github.com/example/" + id, latest,
            [new HackCatalogAppVersion(latest, 4096, [])],
            HackPackageTrust.Verified)
        {
            Kind = HackCatalogItemKind.ThemePack,
        };

    private static HackCatalogApp App(string catalog, string id) =>
        new(catalog, id, "Notes", "utilities", [], new Dictionary<string, string>(), "MIT", "https://github.com/example/notes", "1.0.0",
            [new HackCatalogAppVersion("1.0.0", 1024, [])], HackPackageTrust.Verified);

    private static HackAppCatalog Catalog(string name, params HackCatalogApp[] items) => new(name, HackPackageTrust.Verified, [], items);

    private static HackInstalledThemePack Installed(string id, string source, string version, bool systemWide = false) =>
        new(id, "Installed " + id, version, source, systemWide, HackThemePackParts.Themes);

    [Fact]
    public void Only_theme_packs_are_listed_and_the_apps_tab_ignores_them()
    {
        HackAppCatalog official = Catalog("official", Pack("official", "org.example.neon", "Neon"), App("official", "org.example.notes"));

        StoreThemePacks packs = new([official], []);
        StoreCatalog apps = new([official], [], [], new HashSet<string>());

        Assert.Equal("org.example.neon", Assert.Single(packs.All).PackId);
        Assert.Equal("org.example.notes", Assert.Single(apps.Available).AppId);
    }

    [Fact]
    public void States_follow_the_installed_copy_and_a_newer_catalog_version()
    {
        StoreThemePacks packs = new(
            [Catalog("official", Pack("official", "org.example.neon", "Neon"), Pack("official", "org.example.dusk", "Dusk"), Pack("official", "org.example.mint", "Mint"))],
            [Installed("org.example.neon", "catalog:official/org.example.neon@1.0.0", "1.0.0"),
             Installed("org.example.dusk", "catalog:official/org.example.dusk@1.1.0", "1.1.0", systemWide: true)]);

        Assert.Equal(StoreThemePackState.UpdateAvailable, packs.Find("org.example.neon")!.State);
        Assert.Equal("1.1.0", packs.Find("org.example.neon")!.AvailableUpdate);
        Assert.Equal(StoreThemePackState.Installed, packs.Find("org.example.dusk")!.State);
        Assert.Equal(StoreThemePackState.NotInstalled, packs.Find("org.example.mint")!.State);
        Assert.Equal(["Dusk", "Mint", "Neon"], packs.All.Select(entry => entry.Name));
    }

    [Fact]
    public void The_first_catalog_wins_unless_the_pack_was_installed_from_another()
    {
        HackAppCatalog official = Catalog("official", Pack("official", "org.example.neon", "Neon"));
        HackAppCatalog school = Catalog("school", Pack("school", "org.example.neon", "Neon school"));

        StoreThemePacks fresh = new([official, school], []);
        StoreThemePacks fromSchool = new([official, school], [Installed("org.example.neon", "catalog:school/org.example.neon@1.1.0", "1.1.0")]);

        Assert.Equal("official", Assert.Single(fresh.All).Pack!.Catalog);
        Assert.Equal("school", Assert.Single(fromSchool.All).Pack!.Catalog);
    }

    [Fact]
    public void An_installed_pack_no_catalog_lists_is_still_shown_so_it_can_be_removed()
    {
        StoreThemePacks packs = new([Catalog("official")], [Installed("org.example.gone", "catalog:old/org.example.gone@1.0.0", "1.0.0")]);

        StoreThemePackEntry entry = Assert.Single(packs.All);
        Assert.Null(entry.Pack);
        Assert.Equal("Installed org.example.gone", entry.Name);
        Assert.Equal(StoreThemePackState.Installed, entry.State);
    }

    [Fact]
    public void Search_matches_every_word_in_the_name_description_tags_or_id()
    {
        StoreThemePacks packs = new(
            [Catalog("official", Pack("official", "org.example.neon", "Neon", tags: "cyberpunk"), Pack("official", "org.example.mint", "Mint"))],
            []);

        Assert.Equal(2, packs.Search("", English).Count);
        Assert.Equal("org.example.neon", Assert.Single(packs.Search("neon dark", English)).PackId);
        Assert.Equal("org.example.neon", Assert.Single(packs.Search("CYBERPUNK", English)).PackId);
        Assert.Empty(packs.Search("solarized", English));
    }
}
