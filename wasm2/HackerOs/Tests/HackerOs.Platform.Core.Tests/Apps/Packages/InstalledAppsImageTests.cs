using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Apps.Packages;

/// <summary>Installed apps read at boot (ADR 0182 §5, Q5 answer C).</summary>
public sealed class InstalledAppsImageTests
{
    private static InstalledAppFileReader Reader(Dictionary<string, string> files) =>
        (path, _) => ValueTask.FromResult(files.TryGetValue(path.Value, out string? text) ? Encoding.UTF8.GetBytes(text) : null);

    [Fact]
    public async Task An_installed_app_is_read_from_its_folder()
    {
        InstalledAppsImage image = await InstalledAppsImage.LoadAsync(Reader(new()
        {
            ["/etc/hackeros/apps.config"] = "[installed]\norg.kazo.sample=github:Kazo-ca/Sample@1.0.0\n",
            ["/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/manifest.json"] = AppPackageTests.ManifestJson(),
        }));

        InstalledApp app = Assert.Single(image.Apps);
        Assert.Empty(image.Warnings);
        Assert.Equal("org.kazo.sample", app.AppId);
        Assert.Equal("/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/lib/Kazo.Sample.dll", app.GetAssemblyPath("Kazo.Sample.dll").Value);
        Assert.Equal("/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/wwwroot/data/x.txt", app.GetAssetPath("data/x.txt").Value);
        Assert.Equal(["Kazo.Sample.dll"], app.AssemblyNames);
    }

    [Fact]
    public async Task Without_apps_config_nothing_is_installed()
    {
        InstalledAppsImage image = await InstalledAppsImage.LoadAsync(Reader([]));

        Assert.Empty(image.Apps);
        Assert.Empty(image.Warnings);
    }

    [Fact]
    public async Task A_missing_or_mismatched_manifest_skips_the_app_with_a_warning()
    {
        InstalledAppsImage image = await InstalledAppsImage.LoadAsync(Reader(new()
        {
            ["/etc/hackeros/apps.config"] = "[installed]\norg.kazo.sample=github:Kazo-ca/Sample@2.0.0\norg.kazo.gone=github:Kazo-ca/Gone@1.0.0\n",
            ["/usr/lib/hackeros/apps/org.kazo.sample/2.0.0/manifest.json"] = AppPackageTests.ManifestJson(version: "1.0.0"),
        }));

        Assert.Empty(image.Apps);
        Assert.Equal(2, image.Warnings.Count);
        Assert.Contains(image.Warnings, warning => warning.Contains("org.kazo.gone is listed as installed", StringComparison.Ordinal));
        Assert.Contains(image.Warnings, warning => warning.Contains("not org.kazo.sample 2.0.0", StringComparison.Ordinal));
    }
}
