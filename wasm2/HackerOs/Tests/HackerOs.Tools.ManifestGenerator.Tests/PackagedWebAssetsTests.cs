using System.Security.Cryptography;
using System.Text;
using HackerOs.App.Abstractions;

namespace HackerOs.Tools.ManifestGenerator.Tests;

/// <summary>
/// ADR 0220 §1: <c>HackerOsPack</c> puts every static web resource of the project in the package,
/// from the build's <c>staticwebassets.build.json</c>, and declares the undeclared ones.
/// </summary>
public sealed class PackagedWebAssetsTests
{
    // The shape the .NET 10 SDK writes, trimmed to the fields read: the project's own wwwroot/ file,
    // collocated script and scoped CSS bundle, plus what must be left out.
    private const string BuildManifest = """
        {
          "Version": 1,
          "Source": "Contoso.Notes",
          "Assets": [
            { "Identity": "/src/Contoso.Notes/wwwroot/js/editor.js", "SourceId": "Contoso.Notes", "SourceType": "Discovered", "RelativePath": "js/editor#[.{fingerprint}]?.js", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "", "AssetTraitValue": "" },
            { "Identity": "/src/Contoso.Notes/wwwroot/js/editor.js.gz", "SourceId": "Contoso.Notes", "SourceType": "Discovered", "RelativePath": "js/editor#[.{fingerprint}]?.js.gz", "AssetKind": "All", "AssetRole": "Alternative", "AssetTraitName": "Content-Encoding", "AssetTraitValue": "gzip" },
            { "Identity": "/src/Contoso.Notes/Components/Board.razor.js", "SourceId": "Contoso.Notes", "SourceType": "Discovered", "RelativePath": "Components/Board#[.{fingerprint}]?.razor.js", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "", "AssetTraitValue": "" },
            { "Identity": "/src/Contoso.Notes/obj/scopedcss/projectbundle/Contoso.Notes.bundle.scp.css", "SourceId": "Contoso.Notes", "SourceType": "Computed", "RelativePath": "Contoso.Notes#[.{fingerprint}]!.bundle.scp.css", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "ScopedCss", "AssetTraitValue": "ProjectBundle" },
            { "Identity": "/src/Contoso.Notes/obj/scopedcss/bundle/Contoso.Notes.styles.css", "SourceId": "Contoso.Notes", "SourceType": "Computed", "RelativePath": "Contoso.Notes#[.{fingerprint}]?.styles.css", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "ScopedCss", "AssetTraitValue": "ApplicationBundle" },
            { "Identity": "/src/Contoso.Notes/wwwroot/icons/app.svg", "SourceId": "Contoso.Notes", "SourceType": "Discovered", "RelativePath": "icons/app#[.{fingerprint}]?.svg", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "", "AssetTraitValue": "" },
            { "Identity": "/src/Contoso.Notes/wwwroot/data/odd..name.txt", "SourceId": "Contoso.Notes", "SourceType": "Discovered", "RelativePath": "data/odd..name#[.{fingerprint}]?.txt", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "", "AssetTraitValue": "" },
            { "Identity": "/src/Contoso.Notes/wwwroot/publish-only.json", "SourceId": "Contoso.Notes", "SourceType": "Discovered", "RelativePath": "publish-only.json", "AssetKind": "Publish", "AssetRole": "Primary", "AssetTraitName": "", "AssetTraitValue": "" },
            { "Identity": "/nuget/hackeros.appsdk.blazor/2.2.0/staticwebassets/download.js", "SourceId": "HackerOs.AppSdk.Blazor", "SourceType": "Package", "RelativePath": "download.js", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "", "AssetTraitValue": "" },
            { "Identity": "/src/Shared/Shared.Widgets/wwwroot/widget.js", "SourceId": "Shared.Widgets", "SourceType": "Project", "RelativePath": "widget#[.{fingerprint}]?.js", "AssetKind": "All", "AssetRole": "Primary", "AssetTraitName": "", "AssetTraitValue": "" }
          ]
        }
        """;

    private static readonly Dictionary<string, byte[]> Files = new(StringComparer.Ordinal)
    {
        ["/src/Contoso.Notes/wwwroot/js/editor.js"] = Encoding.UTF8.GetBytes("export const editor = 1;"),
        ["/src/Contoso.Notes/Components/Board.razor.js"] = Encoding.UTF8.GetBytes("export function attach() {}"),
        ["/src/Contoso.Notes/obj/scopedcss/projectbundle/Contoso.Notes.bundle.scp.css"] = Encoding.UTF8.GetBytes(".board[b-abc] { display: grid; }"),
        ["/src/Contoso.Notes/wwwroot/icons/app.svg"] = Encoding.UTF8.GetBytes("<svg/>"),
    };

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static AppManifest Manifest(params AssetManifest[] assets) => AppManifestJsonSerializer.NormalizeOptionalCollections(AppManifestJsonSerializer.DeserializeStrict(
        "{\"schemaVersion\":1,\"id\":\"org.contoso.notes\",\"name\":\"Notes\",\"version\":\"1.0.0\",\"publisherId\":\"pub.contoso\","
        + "\"description\":\"Notes.\",\"kind\":\"window\",\"platform\":{\"supported\":[\"desktop\"],\"entryPoints\":[{\"platforms\":[\"desktop\"],"
        + "\"assembly\":\"Contoso.Notes.dll\",\"type\":\"Contoso.Notes.NotesWindow\"}]},\"sdkCompatibility\":{\"minimumVersion\":\"2.0.0\"},"
        + "\"presentation\":{\"category\":\"utilities\",\"launchVisibility\":\"visible\",\"iconAssetPaths\":[]},\"localizations\":[],\"capabilities\":[],"
        + "\"declaredTopicPermissions\":[],\"resources\":{\"baselineCpuWeight\":0,\"burstCpuWeight\":0,\"baselineMemoryWeight\":0,\"burstMemoryWeight\":0,"
        + "\"baselineStorageIoWeight\":0,\"burstStorageIoWeight\":0,\"baselineNetworkIoWeight\":0,\"burstNetworkIoWeight\":0},\"intents\":[],\"dependencies\":[],"
        + "\"assets\":[],\"fileHandlers\":[],\"singleInstancePerUser\":false,\"autoStart\":false,\"supportsBack\":false,\"execution\":\"mainThread\"}")) with { Assets = assets };

    [Fact]
    public void The_project_own_resources_are_read_with_their_plain_served_paths()
    {
        PackagedWebAssets.ReadResult result = PackagedWebAssets.Read(BuildManifest);

        Assert.Equal(
            ["Components/Board.razor.js", "Contoso.Notes.bundle.scp.css", "icons/app.svg", "js/editor.js"],
            result.Assets.Select(static asset => asset.Path));
        Assert.Equal("/src/Contoso.Notes/Components/Board.razor.js", result.Assets[0].SourceFile);
    }

    [Fact]
    public void Referenced_compressed_publish_only_and_application_bundle_resources_are_left_out()
    {
        string[] paths = [.. PackagedWebAssets.Read(BuildManifest).Assets.Select(static asset => asset.Path)];

        Assert.DoesNotContain("download.js", paths);
        Assert.DoesNotContain("widget.js", paths);
        Assert.DoesNotContain("js/editor.js.gz", paths);
        Assert.DoesNotContain("publish-only.json", paths);
        Assert.DoesNotContain("Contoso.Notes.styles.css", paths);
    }

    [Fact]
    public void A_path_a_manifest_refuses_is_skipped_with_a_reason()
    {
        PackagedWebAssets.ReadResult result = PackagedWebAssets.Read(BuildManifest);

        string skipped = Assert.Single(result.Skipped);
        Assert.Contains("data/odd..name.txt", skipped, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Assets, static asset => asset.Path.Contains("odd", StringComparison.Ordinal));
    }

    [Fact]
    public void Undeclared_resources_are_added_with_their_hash_and_kind_and_declared_ones_are_kept()
    {
        AssetManifest icon = new("icons/app.svg", AssetKind.Image, Hash(Files["/src/Contoso.Notes/wwwroot/icons/app.svg"]), "icon-32");
        PackagedWebAssets.ReadResult result = PackagedWebAssets.Read(BuildManifest);

        AppManifest packaged = PackagedWebAssets.Complete(Manifest(icon), result.Assets, path => Files[path]);

        Assert.Equal(icon, packaged.Assets[0]);
        Assert.Equal(4, packaged.Assets.Count);
        AssetManifest styles = packaged.Assets.Single(static asset => asset.Path == "Contoso.Notes.bundle.scp.css");
        Assert.Equal(AssetKind.Css, styles.Kind);
        Assert.Equal(Hash(Files["/src/Contoso.Notes/obj/scopedcss/projectbundle/Contoso.Notes.bundle.scp.css"]), styles.Sha256);
        Assert.Equal(AssetKind.JavaScriptModule, packaged.Assets.Single(static asset => asset.Path == "Components/Board.razor.js").Kind);
        Assert.True(AppManifestValidator.Validate(packaged).IsValid);
    }

    [Fact]
    public void A_manifest_that_already_declares_everything_is_returned_unchanged()
    {
        PackagedWebAssets.ReadResult result = PackagedWebAssets.Read(BuildManifest);
        AppManifest complete = PackagedWebAssets.Complete(Manifest(), result.Assets, path => Files[path]);

        Assert.Same(complete, PackagedWebAssets.Complete(complete, result.Assets, path => Files[path]));
    }

    [Theory]
    [InlineData("pieces/k.svg", AssetKind.Image)]
    [InlineData("photo.WEBP", AssetKind.Image)]
    [InlineData("App.bundle.scp.css", AssetKind.Css)]
    [InlineData("engine/worker.mjs", AssetKind.JavaScriptModule)]
    [InlineData("engine/engine.wasm", AssetKind.Data)]
    [InlineData("LICENSE", AssetKind.Data)]
    public void The_kind_comes_from_the_extension(string path, AssetKind expected) =>
        Assert.Equal(expected, PackagedWebAssets.KindOf(path));

    [Theory]
    [InlineData("js/app#[.{fingerprint}]?.js", "js/app.js")]
    [InlineData("App#[.{fingerprint}]!.bundle.scp.css", "App.bundle.scp.css")]
    [InlineData("engine/COPYING#[.{fingerprint}]?", "engine/COPYING")]
    [InlineData("plain/file.txt", "plain/file.txt")]
    public void The_fingerprint_pattern_is_removed_from_the_served_path(string relativePath, string expected) =>
        Assert.Equal(expected, PackagedWebAssets.ServedPath(relativePath));

    [Fact]
    public void A_text_that_is_not_a_static_web_asset_manifest_is_refused() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => PackagedWebAssets.Read("{\"Version\":1}"));
}
