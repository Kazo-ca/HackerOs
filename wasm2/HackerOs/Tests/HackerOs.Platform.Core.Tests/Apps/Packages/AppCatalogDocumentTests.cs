using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary><c>catalog.json</c> (ADR 0188 §6) and the <c>catalog:</c> source form (ADR 0212 §1).</summary>
public sealed class AppCatalogDocumentTests
{
    private static readonly Uri Address = new("https://example.org/catalog/packages/catalog.json");

    private static string Version(string version, string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string package = "") =>
        $$$"""{"version":"{{{version}}}","package":"{{{(package.Length > 0 ? package : $"packages/org.example.notes/{version}/org.example.notes-{version}.hackapp")}}}","sha256":"{{{sha}}}","size":10,"capabilities":["storage.app"],"publishedAt":"2026-10-30T12:00:00Z","icon":"packages/org.example.notes/{{{version}}}/icon.svg","screenshots":["../../etc/passwd","packages/org.example.notes/{{{version}}}/1.png"]}""";

    private static AppCatalogListing Parse(string apps, int schema = 1) =>
        AppCatalogDocument.Parse("official", Address, Encoding.UTF8.GetBytes(
            $$$"""{"schemaVersion":{{{schema}}},"categories":[{"id":"utilities","labels":{"en":"Utilities"}}],"apps":[{{{apps}}}]}"""));

    [Fact]
    public void A_valid_catalog_lists_its_apps_newest_version_first()
    {
        AppCatalogListing listing = Parse(
            $$$"""{"appId":"org.example.notes","name":"Notes","category":"utilities","license":"MIT","latestVersion":"1.1.0","versions":[{{{Version("1.0.0")}}},{{{Version("1.1.0")}}}]}""");

        AppCatalogEntry app = Assert.Single(listing.Apps);
        Assert.Equal(["1.1.0", "1.0.0"], app.Versions.Select(version => version.Version.ToString()));
        Assert.Equal(SemanticVersion.Parse("1.1.0"), app.LatestVersion);
        Assert.Equal("utilities", Assert.Single(listing.Categories).Id);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero), app.Latest.PublishedAt);
        // A path that leaves the catalog is dropped.
        Assert.Equal(["packages/org.example.notes/1.1.0/1.png"], app.Latest.Screenshots);
        Assert.Empty(listing.Warnings);
    }

    [Fact]
    public void Files_resolve_next_to_catalog_json()
    {
        AppCatalogListing listing = Parse(
            $$$"""{"appId":"org.example.notes","name":"Notes","versions":[{{{Version("1.0.0")}}}]}""");

        Assert.Equal(
            "https://example.org/catalog/packages/packages/org.example.notes/1.0.0/icon%20a.svg",
            listing.Resolve("packages/org.example.notes/1.0.0/icon a.svg").AbsoluteUri);
    }

    [Theory]
    [InlineData("""{"appId":"Bad Id","name":"x","versions":[]}""")]
    [InlineData("""{"appId":"org.example.theme","kind":"theme","name":"x","versions":[]}""")]
    [InlineData("""{"appId":"org.example.empty","name":"x","versions":[]}""")]
    public void A_bad_entry_is_skipped_with_a_warning(string app)
    {
        AppCatalogListing listing = Parse(app);

        Assert.Empty(listing.Apps);
        Assert.NotEmpty(listing.Warnings);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("../escape.hackapp")]
    public void A_version_with_a_bad_hash_or_path_is_skipped(string bad)
    {
        string version = bad == "short" ? Version("1.0.0", sha: "abc") : Version("1.0.0", package: bad);

        AppCatalogListing listing = Parse($$$"""{"appId":"org.example.notes","name":"Notes","versions":[{{{version}}}]}""");

        Assert.Empty(listing.Apps);
    }

    [Fact]
    public void A_newer_schema_is_refused()
    {
        AppPackageException error = Assert.Throws<AppPackageException>(() => Parse(string.Empty, schema: 2));

        Assert.Equal("apps.catalog.unsupported", error.Code);
    }

    [Fact]
    public void Invalid_json_is_refused()
    {
        AppPackageException error = Assert.Throws<AppPackageException>(
            () => AppCatalogDocument.Parse("official", Address, Encoding.UTF8.GetBytes("{not json")));

        Assert.Equal("apps.catalog.invalid", error.Code);
    }

    [Fact]
    public void A_catalog_source_parses_and_prints_back()
    {
        Assert.True(AppPackageSource.TryParse("catalog:Official/org.example.notes@1.2.0", out AppPackageSource? source));

        Assert.True(source.IsCatalog);
        Assert.Equal("official", source.Catalog);
        Assert.Equal("org.example.notes", source.CatalogAppId);
        Assert.Equal("catalog:official/org.example.notes@1.2.0", source.ToString());
        Assert.Equal("catalog:official", source.TrustKey);
        Assert.True(AppPackageSource.TryParse("github:official/org.example.notes", out AppPackageSource? github));
        Assert.NotEqual(AppPackageSource.ForCatalog("official", "org.example.notes"), github);
        Assert.Throws<InvalidOperationException>(() => source.GetFileUri("raw.githubusercontent.com", "index.json"));
    }

    [Theory]
    [InlineData("catalog:official/Notes")]
    [InlineData("catalog:/org.example.notes")]
    [InlineData("catalog:official/org.example")]
    public void A_bad_catalog_source_is_refused(string text)
    {
        Assert.False(AppPackageSource.TryParse(text, out _));
    }
}
