using System.Text;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Tests.Apps.ThemePacks;

namespace HackerOs.Platform.Core.Tests.Execution;

/// <summary>
/// Theme packs of the Hack Store through <c>HackAppPackages</c>' gateway (ADR 0213): unpacked as
/// they are in <c>theme-packs/&lt;id&gt;/</c>, for the session user without an administrator or
/// for every user with ADR 0189's two-step approval.
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    private const string Pack = ThemePackTests.PackId;
    private const string PersonalPack = "/home/alice/.local/share/hackeros/theme-packs/" + Pack;
    private const string SystemPack = "/usr/share/hackeros/theme-packs/" + Pack;

    [Fact]
    public async Task A_theme_pack_of_the_official_catalog_is_installed_for_the_user_without_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: approval);

        HackThemePackInstallReport report = await gateway.InstallThemePackAsync(Pack);

        Assert.Equal(0, agent.Calls);
        Assert.Empty(approval.Requests);
        Assert.False(report.SystemWide);
        Assert.False(report.RestartRequired);
        Assert.Equal(HackPackageTrust.Verified, report.Trust);
        Assert.Equal($"catalog:official/{Pack}@1.0.0", report.Source);
        Assert.Equal(HackThemePackParts.Themes | HackThemePackParts.Wallpapers | HackThemePackParts.Icons | HackThemePackParts.Sounds | HackThemePackParts.Cursors, report.Parts);
        Assert.Contains("--hos-accent", await harness.Settings.ReadAsync($"{PersonalPack}/themes/neon-night.theme"), StringComparison.Ordinal);
        Assert.Contains("version=1.0.0", await harness.Settings.ReadAsync($"{PersonalPack}/{AppPackageInstaller.InstallRecordFileName}"), StringComparison.Ordinal);
        Assert.Null(await harness.Settings.StatAsync(SystemPack));
        HackInstalledThemePack installed = Assert.Single(await gateway.GetInstalledThemePacksAsync());
        Assert.Equal(new HackInstalledThemePack(Pack, "Neon", "1.0.0", $"catalog:official/{Pack}@1.0.0", false, report.Parts), installed);
    }

    [Fact]
    public async Task The_catalog_lists_a_theme_pack_with_its_kind_and_the_store_apps_without_it()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.0.0");

        HackAppCatalog official = Assert.Single(await (await harness.CreateGatewayAsync(new CountingAgent(succeed: true))).GetPackageCatalogsAsync());

        HackCatalogApp pack = Assert.Single(official.Apps);
        Assert.Equal(HackCatalogItemKind.ThemePack, pack.Kind);
        Assert.Equal($"catalog:official/{Pack}", pack.Source);
    }

    [Fact]
    public async Task A_theme_pack_is_not_an_app_and_an_app_is_not_a_theme_pack()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        HackPackageException asApp = await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Pack).AsTask());
        HackPackageException unknown = await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallThemePackAsync("org.example.missing").AsTask());

        Assert.Equal("apps.package.not-in-catalog", asApp.Code);
        Assert.Equal("themes.pack.not-in-catalog", unknown.Code);
    }

    [Fact]
    public async Task A_system_theme_pack_goes_through_the_two_steps_and_replaces_the_personal_copy()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: approval);
        await gateway.InstallThemePackAsync(Pack, HackPackageScope.User);

        HackThemePackInstallReport report = await gateway.InstallThemePackAsync(Pack, HackPackageScope.System);

        Assert.True(report.SystemWide);
        Assert.Equal(1, agent.Calls);
        AppInstallApprovalRequest request = Assert.Single(approval.Requests);
        Assert.True(request.SystemWide);
        Assert.Empty(request.Capabilities);
        Assert.NotNull(await harness.Settings.StatAsync($"{SystemPack}/cursors/cursor.theme"));
        Assert.Null(await harness.Settings.StatAsync(PersonalPack));
        Assert.True(Assert.Single(await gateway.GetInstalledThemePacksAsync()).SystemWide);
    }

    [Fact]
    public async Task A_theme_pack_update_replaces_the_previous_files()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.1.0", "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        await gateway.InstallThemePackAsync(Pack + "@1.0.0");
        Assert.NotNull(await harness.Settings.StatAsync($"{PersonalPack}/wallpapers/grid.png"));

        HackThemePackInstallReport report = await gateway.InstallThemePackAsync(Pack);

        Assert.Equal("1.1.0", report.Version);
        Assert.Equal("1.0.0", report.PreviousVersion);
        // Version 1.1.0 dropped its wallpaper: the old file must not stay behind.
        Assert.Null(await harness.Settings.StatAsync($"{PersonalPack}/wallpapers/grid.png"));
        Assert.Equal("1.1.0", Assert.Single(await gateway.GetInstalledThemePacksAsync()).Version);
        Assert.Null(await harness.Settings.StatAsync("/home/alice/.local/share/hackeros/theme-packs.partial/" + Pack));
    }

    [Fact]
    public async Task A_blocked_theme_pack_cannot_be_installed_for_oneself()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.0.0");
        await harness.Settings.WriteAsRootAsync(AppPolicyConfigDocument.MachinePath.Value, $"[blocked-apps]\n{Pack}=true\n");
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: approval);

        HackPackageException refused = await Assert.ThrowsAsync<HackPackageException>(
            () => gateway.InstallThemePackAsync(Pack, HackPackageScope.User).AsTask());
        HackThemePackInstallReport report = await gateway.InstallThemePackAsync(Pack);

        Assert.Equal("apps.package.blocked", refused.Code);
        Assert.True(report.SystemWide);
        Assert.NotNull(Assert.Single(approval.Requests).BlockReason);
    }

    [Fact]
    public async Task A_theme_pack_whose_hash_does_not_match_is_refused_before_the_prompt()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.0.0", corruptHash: true);
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        HackPackageException error = await Assert.ThrowsAsync<HackPackageException>(
            () => gateway.InstallThemePackAsync(Pack, HackPackageScope.System).AsTask());

        Assert.Equal("apps.package.hash-mismatch", error.Code);
        Assert.Equal(0, agent.Calls);
        Assert.Null(await harness.Settings.StatAsync(SystemPack));
    }

    [Fact]
    public async Task A_personal_theme_pack_is_removed_without_an_administrator_and_a_system_one_with_the_two_steps()
    {
        Harness harness = await Harness.CreateAsync();
        harness.PublishThemePackCatalog("1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: approval);
        await gateway.InstallThemePackAsync(Pack);

        HackInstalledThemePack personal = await gateway.RemoveThemePackAsync(Pack);

        Assert.False(personal.SystemWide);
        Assert.Equal(0, agent.Calls);
        Assert.Null(await harness.Settings.StatAsync(PersonalPack));

        await gateway.InstallThemePackAsync(Pack, HackPackageScope.System);
        HackInstalledThemePack system = await gateway.RemoveThemePackAsync(Pack);

        Assert.True(system.SystemWide);
        Assert.Equal(AppInstallApprovalOperation.Remove, approval.Requests[^1].Operation);
        Assert.Null(await harness.Settings.StatAsync(SystemPack));
        Assert.Empty(await gateway.GetInstalledThemePacksAsync());
    }

    private sealed partial class Harness
    {
        /// <summary>
        /// Publishes the sample theme pack in the official catalog (<c>kind: theme-pack</c>, ADR
        /// 0213 §2), newest version first. Versions after the oldest drop the wallpaper, so an
        /// update test sees old files go away.
        /// </summary>
        internal void PublishThemePackCatalog(string latest, params string[] older) =>
            PublishThemePackCatalog(latest, false, older);

        internal void PublishThemePackCatalog(string latest, bool corruptHash, params string[] older)
        {
            string root = AppPackageDefaults.OfficialCatalogAddress[..^"catalog.json".Length];
            string oldest = older.Length == 0 ? latest : older[^1];
            List<string> versions = [];
            foreach (string version in older.Prepend(latest))
            {
                Dictionary<string, byte[]> files = ThemePackTests.FullPack();
                if (version != oldest)
                {
                    files.Remove("wallpapers/grid.png");
                }

                byte[] pack = ThemePackTests.Build(files, version: version);
                string folder = $"packages/{Pack}/{version}";
                _downloader.Files[new Uri($"{root}{folder}/{Pack}-{version}.hacktheme")] = pack;
                string sha = corruptHash ? new string('0', 64) : AppPackage.Sha256(pack);
                versions.Add($$$"""
                    {"version":"{{{version}}}","commit":"abc","package":"{{{folder}}}/{{{Pack}}}-{{{version}}}.hacktheme",
                     "sha256":"{{{sha}}}","size":{{{pack.Length}}},"publishedAt":"2026-10-31T00:00:00Z",
                     "icon":"{{{folder}}}/preview.png","descriptions":{}}
                    """);
            }

            _downloader.Files[new Uri(AppPackageDefaults.OfficialCatalogAddress)] = Encoding.UTF8.GetBytes($$$"""
                {"schemaVersion":1,"categories":[{"id":"themes","labels":{"en":"Themes","fr":"Thèmes"}}],
                 "apps":[{"appId":"{{{Pack}}}","kind":"theme-pack","name":"Neon","category":"themes","tags":["dark"],
                          "shortDescriptions":{"en":"Neon pack"},"license":"CC-BY-4.0","repository":"https://github.com/example/neon",
                          "latestVersion":"{{{latest}}}","versions":[{{{string.Join(',', versions)}}}]}]}
                """);
        }
    }
}
