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

    private static InstalledAppHomeLister Homes(params string[] homes) => _ => ValueTask.FromResult<IReadOnlyList<string>>(homes);

    [Fact]
    public async Task A_personal_app_is_read_from_its_owner_home_and_seen_by_that_user_only()
    {
        InstalledAppsImage image = await InstalledAppsImage.LoadAsync(
            Reader(new()
            {
                ["/home/alice/.config/hackeros/installed-apps.config"] = "[installed]\norg.kazo.sample=github:Kazo-ca/Sample@1.0.0\n",
                ["/home/alice/.local/share/hackeros/apps/org.kazo.sample/1.0.0/manifest.json"] = AppPackageTests.ManifestJson(),
            }),
            Homes("/home/alice", "/home/bob"));

        InstalledApp app = Assert.Single(image.Apps);
        Assert.Empty(image.Warnings);
        Assert.False(app.IsSystemWide);
        Assert.Equal(["/home/alice"], app.OwnerHomes);
        Assert.Equal("/home/alice/.local/share/hackeros/apps/org.kazo.sample/1.0.0/lib/Kazo.Sample.dll", app.GetAssemblyPath("Kazo.Sample.dll").Value);

        InstalledPackageCatalog catalog = new();
        catalog.Apply(image.Apps);
        Assert.True(catalog.IsVisibleTo("org.kazo.sample", "/home/alice/"));
        Assert.False(catalog.IsVisibleTo("org.kazo.sample", "/home/bob"));
        Assert.False(catalog.IsVisibleTo("org.kazo.sample", null));
        Assert.True(catalog.IsVisibleTo("org.hackeros.calculator", "/home/bob"));
        Assert.Equal(["org.kazo.sample"], catalog.GetHiddenFor("/home/bob"));
        Assert.Empty(catalog.GetHiddenFor("/home/alice"));
    }

    [Fact]
    public async Task A_personal_copy_of_a_system_app_is_skipped()
    {
        InstalledAppsImage image = await InstalledAppsImage.LoadAsync(
            Reader(new()
            {
                ["/etc/hackeros/apps.config"] = "[installed]\norg.kazo.sample=github:Kazo-ca/Sample@1.0.0\n",
                ["/usr/lib/hackeros/apps/org.kazo.sample/1.0.0/manifest.json"] = AppPackageTests.ManifestJson(),
                ["/home/alice/.config/hackeros/installed-apps.config"] = "[installed]\norg.kazo.sample=github:Kazo-ca/Sample@1.0.0\n",
            }),
            Homes("/home/alice"));

        Assert.True(Assert.Single(image.Apps).IsSystemWide);
        Assert.Contains("also installed for the system", Assert.Single(image.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_users_with_the_same_app_both_get_the_newest_copy()
    {
        InstalledAppsImage image = await InstalledAppsImage.LoadAsync(
            Reader(new()
            {
                ["/home/alice/.config/hackeros/installed-apps.config"] = "[installed]\norg.kazo.sample=github:Kazo-ca/Sample@1.0.0\n",
                ["/home/bob/.config/hackeros/installed-apps.config"] = "[installed]\norg.kazo.sample=github:Kazo-ca/Sample@1.1.0\n",
                ["/home/bob/.local/share/hackeros/apps/org.kazo.sample/1.1.0/manifest.json"] = AppPackageTests.ManifestJson(version: "1.1.0"),
            }),
            Homes("/home/alice", "/home/bob"));

        InstalledApp app = Assert.Single(image.Apps);
        Assert.Equal("1.1.0", app.Manifest.Version);
        Assert.Equal("/home/bob", app.FilesHome);
        Assert.Equal(["/home/alice", "/home/bob"], app.OwnerHomes.Order(StringComparer.Ordinal));
        Assert.Contains("installed by 2 users", Assert.Single(image.Warnings), StringComparison.Ordinal);
    }
}
