using System.Xml.Linq;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;

namespace Kazo.Os.AppSdk.Tests.Apps;

/// <summary>
/// ADR 0232: the URL of an app's static files, the <see cref="KazoAppInfo.IconUrl"/> the Start menu, the
/// desktop and the file control draw, and the illustrated style of the system apps' icons.
/// </summary>
public sealed class KazoAppAssetsTests
{
    [Theory]
    [InlineData("Contoso.Notes", "icons/app.svg", "_content/Contoso.Notes/icons/app.svg")]
    [InlineData("Contoso.Notes.dll", "icons/app.svg", "_content/Contoso.Notes/icons/app.svg")]
    [InlineData("Contoso.Notes", "icons/my app#1.svg", "_content/Contoso.Notes/icons/my%20app%231.svg")]
    public void RelativeUrl_serves_the_asset_under_the_assembly_static_assets(string assembly, string path, string expected)
    {
        // Arrange
        AppManifest manifest = Manifest(assembly, []);

        // Act
        string url = KazoAppAssets.RelativeUrl(manifest, path);

        // Assert
        Assert.Equal(expected, url);
    }

    [Fact]
    public void RelativeUrl_refuses_an_app_without_an_assembly()
    {
        AppManifest manifest = Manifest(assembly: null, ["icons/app.svg"]);

        Assert.Throws<InvalidOperationException>(() => KazoAppAssets.RelativeUrl(manifest, "icons/app.svg"));
    }

    [Fact]
    public void IconUrl_points_at_the_first_declared_icon()
    {
        AppManifest manifest = Manifest("Contoso.Notes", ["icons/app.svg", "icons/app-large.svg"]);

        KazoAppInfo info = KazoAppInfo.FromManifest(manifest, isSystemApp: false);

        Assert.Equal("icons/app.svg", info.Icon);
        Assert.Equal("_content/Contoso.Notes/icons/app.svg", info.IconUrl);
    }

    [Theory]
    [InlineData("Contoso.Notes", false)]
    [InlineData(null, true)]
    public void IconUrl_is_null_without_an_icon_or_an_assembly(string? assembly, bool declaresIcon)
    {
        AppManifest manifest = Manifest(assembly, declaresIcon ? ["icons/app.svg"] : []);

        KazoAppInfo info = KazoAppInfo.FromManifest(manifest, isSystemApp: true);

        Assert.Null(info.IconUrl);
    }

    [Fact]
    public void Every_system_app_icon_is_an_illustrated_tile_drawn_for_the_project()
    {
        // Arrange: Apps/System/*/wwwroot/icons/app.svg, found above the test output.
        string[] icons = Directory.GetFiles(FindSystemAppsDirectory(), "app.svg", SearchOption.AllDirectories)
            .Where(path => path.Replace('\\', '/').EndsWith("/wwwroot/icons/app.svg", StringComparison.Ordinal))
            .ToArray();
        Assert.True(icons.Length >= 15, $"Only {icons.Length} system app icons found.");

        foreach (string path in icons)
        {
            string text = File.ReadAllText(path);

            // Act
            XDocument svg = XDocument.Parse(text);
            XNamespace ns = "http://www.w3.org/2000/svg";
            XElement tile = svg.Root!.Element(ns + "rect")!;

            // Assert: ADR 0232 §1 and §5.
            Assert.True(text.Length < 3 * 1024, $"{path} is larger than 3 KB.");
            Assert.Contains("ADR 0232", text, StringComparison.Ordinal);
            Assert.Contains("same licence as this repository", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Lucide", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<script", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("href", text, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("0 0 32 32", svg.Root.Attribute("viewBox")?.Value);
            Assert.Equal("7", tile.Attribute("rx")?.Value);
            Assert.Equal("url(#tile)", tile.Attribute("fill")?.Value);
            Assert.Contains("stop-color=\"#26303a\"", text, StringComparison.Ordinal);
            Assert.Contains("stop-color=\"#0a0e12\"", text, StringComparison.Ordinal);
        }
    }

    private static AppManifest Manifest(string? assembly, IReadOnlyList<string> icons) => new()
    {
        Id = "org.contoso.notes",
        Name = "Notes",
        Version = "1.0.0",
        PublisherId = "org.contoso",
        Description = "Icon URL test application.",
        Kind = AppKind.Window,
        EntryPoint = assembly is null ? null : new AppEntryPointManifest(assembly, "Contoso.Notes.NotesWindow"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("utilities", AppLaunchVisibility.Visible, icons),
        Resources = AppResourceProfileManifest.None
    };

    private static string FindSystemAppsDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Apps", "System");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Apps/System not found above the test output.");
    }
}
