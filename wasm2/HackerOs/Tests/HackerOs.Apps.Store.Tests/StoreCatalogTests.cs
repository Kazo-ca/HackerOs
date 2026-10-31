using System.Globalization;
using HackerOs.AppSdk.Apps;
using Xunit;

namespace HackerOs.Apps.Store.Tests;

public sealed class StoreCatalogTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr");

    private static HackCatalogApp App(string catalog, string appId, string name, string category, string description = "", params string[] tags) =>
        new(catalog, appId, name, category, tags,
            new Dictionary<string, string> { ["en"] = description, ["fr"] = "fr " + description },
            "MIT", "https://github.com/example/" + name, "1.1.0",
            [new HackCatalogAppVersion("1.1.0", 2048, ["storage.app"]), new HackCatalogAppVersion("1.0.0", 1024, [])],
            HackPackageTrust.Verified);

    private static HackAppCatalog Catalog(string name, params HackCatalogApp[] apps) =>
        new(name, HackPackageTrust.Verified,
            [new HackCatalogCategory("utilities", new Dictionary<string, string> { ["en"] = "Utilities", ["fr"] = "Utilitaires" }),
             new HackCatalogCategory("graphics", new Dictionary<string, string> { ["en"] = "Graphics" })],
            apps);

    [Fact]
    public void An_app_listed_by_two_catalogs_shows_the_first_catalog()
    {
        StoreCatalog store = new(
            [Catalog("official", App("official", "org.example.notes", "Notes", "utilities")),
             Catalog("school", App("school", "org.example.notes", "Notes school", "utilities"))],
            [], [], new HashSet<string>());

        Assert.Equal("official", Assert.Single(store.Available).App!.Catalog);
    }

    [Fact]
    public void An_installed_copy_keeps_the_page_of_its_catalog()
    {
        StoreCatalog store = new(
            [Catalog("official", App("official", "org.example.notes", "Notes", "utilities")),
             Catalog("school", App("school", "org.example.notes", "Notes school", "utilities"))],
            [new HackInstalledPackage("org.example.notes", "catalog:school/org.example.notes", "1.0.0", true)],
            [], new HashSet<string>());

        Assert.Equal("school", store.Find("org.example.notes")!.App!.Catalog);
    }

    [Fact]
    public void States_follow_install_update_and_restart()
    {
        StoreCatalog store = new(
            [Catalog("official",
                App("official", "org.example.a", "A", "utilities"),
                App("official", "org.example.b", "B", "utilities"),
                App("official", "org.example.c", "C", "utilities"),
                App("official", "org.example.d", "D", "utilities"),
                App("official", "org.example.e", "E", "utilities"))],
            [new HackInstalledPackage("org.example.b", "catalog:official/org.example.b", "1.1.0", true),
             new HackInstalledPackage("org.example.c", "catalog:official/org.example.c", "1.0.0", true),
             new HackInstalledPackage("org.example.d", "catalog:official/org.example.d", "1.1.0", false),
             new HackInstalledPackage("org.example.e", "catalog:official/org.example.e", "1.1.0", true)],
            [new HackPackageUpdate("org.example.c", "catalog:official/org.example.c", "1.0.0", "1.1.0", true)],
            new HashSet<string> { "org.example.e" });

        Assert.Equal(StoreAppState.NotInstalled, store.Find("org.example.a")!.State);
        Assert.Equal(StoreAppState.Installed, store.Find("org.example.b")!.State);
        Assert.Equal(StoreAppState.UpdateAvailable, store.Find("org.example.c")!.State);
        Assert.Equal(StoreAppState.PendingRestart, store.Find("org.example.d")!.State);
        Assert.Equal(StoreAppState.PendingRestart, store.Find("org.example.e")!.State);
        Assert.True(store.HasPendingRestart);
        Assert.Equal("org.example.c", Assert.Single(store.Updatable).AppId);
        Assert.Equal(["B", "C", "D", "E"], store.Installed.Select(entry => entry.Name));
    }

    [Fact]
    public void A_system_copy_wins_over_a_personal_copy()
    {
        StoreCatalog store = new(
            [Catalog("official", App("official", "org.example.a", "A", "utilities"))],
            [new HackInstalledPackage("org.example.a", "catalog:official/org.example.a", "1.0.0", true) { SystemWide = false },
             new HackInstalledPackage("org.example.a", "catalog:official/org.example.a", "1.1.0", true) { SystemWide = true }],
            [], new HashSet<string>());

        Assert.True(store.Find("org.example.a")!.Installed!.SystemWide);
    }

    [Fact]
    public void An_installed_app_no_catalog_lists_is_still_in_the_installed_tab()
    {
        StoreCatalog store = new(
            [Catalog("official")],
            [new HackInstalledPackage("org.example.gone", "github:example/gone", "1.0.0", true)],
            [], new HashSet<string>());

        StoreEntry entry = Assert.Single(store.Installed);
        Assert.Null(entry.App);
        Assert.Equal("org.example.gone", entry.Name);
        Assert.Empty(store.Available);
    }

    [Fact]
    public void Home_lists_categories_with_apps_then_the_others()
    {
        StoreCatalog store = new(
            [Catalog("official",
                App("official", "org.example.a", "Alpha", "utilities"),
                App("official", "org.example.b", "Beta", "unknown"))],
            [], [], new HashSet<string>());

        var home = store.Home();

        Assert.Equal(["utilities", StoreCatalog.OtherCategoryId], home.Select(section => section.Category.Id));
        Assert.Equal("Beta", Assert.Single(home[1].Apps).Name);
    }

    [Theory]
    [InlineData("notes", 1)]
    [InlineData("NOTÉS", 1)]
    [InlineData("markdown", 1)]
    [InlineData("fr quick", 1)]
    [InlineData("org.example", 2)]
    [InlineData("nothing", 0)]
    [InlineData("  ", 2)]
    public void Search_ignores_case_and_accents_and_matches_every_word(string query, int expected)
    {
        StoreCatalog store = new(
            [Catalog("official",
                App("official", "org.example.notes", "Notes", "utilities", "quick notes", "markdown"),
                App("official", "org.example.paint", "Paint", "graphics", "draw"))],
            [], [], new HashSet<string>());

        Assert.Equal(expected, store.Search(query, French).Count);
    }

    [Fact]
    public void Failed_catalogs_are_reported()
    {
        StoreCatalog store = new(
            [Catalog("official"), new HackAppCatalog("school", HackPackageTrust.Trusted, [], []) { Error = "offline" }],
            [], [], new HashSet<string>());

        Assert.Equal("school", Assert.Single(store.FailedCatalogs).Name);
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1572864, "1.5 MB")]
    public void FormatSize_uses_bytes_KB_and_MB(long bytes, string expected)
    {
        Assert.Equal(expected, StoreCatalog.FormatSize(bytes, CultureInfo.InvariantCulture));
    }
}
