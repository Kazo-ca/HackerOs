using System.Security.Cryptography;
using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk.Apps;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Tests.Apps.Packages;

namespace HackerOs.Platform.Core.Tests.Execution;

/// <summary>
/// ADR 0214: signed packages, transactional installs (journal, <c>.partial</c> folder, repair),
/// the version kept for a rollback and <c>update.minimumUpgradableVersion</c>.
/// </summary>
public sealed partial class AppPackageGatewayTests
{
    private const string SystemConfig = "/etc/hackeros/apps.config";

    [Fact]
    public async Task A_rollback_puts_back_the_kept_version_and_keeps_the_one_it_leaves()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        harness.Publish(Trusted, "1.0.0");
        await gateway.InstallPackageAsync(Trusted);
        harness.Publish(Trusted, "1.1.0", "1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.1.0");

        HackInstalledPackage before = Assert.Single(await gateway.GetInstalledPackagesAsync());
        HackPackageInstallReport back = await gateway.RollbackPackageAsync(AppPackageTests.AppId);
        HackInstalledPackage after = Assert.Single(await gateway.GetInstalledPackagesAsync());
        HackPackageInstallReport again = await gateway.RollbackPackageAsync(AppPackageTests.AppId);

        Assert.Equal("1.0.0", before.PreviousVersion);
        Assert.Equal(("1.0.0", "1.1.0"), (back.Version, back.PreviousVersion));
        Assert.Equal(("1.0.0", "1.1.0"), (after.Version, after.PreviousVersion));
        Assert.Equal(("1.1.0", "1.0.0"), (again.Version, again.PreviousVersion));
        Assert.Contains(
            "org.kazo.sample=github:Kazo-ca/HackerOs-App-Sample@1.1.0", await harness.Settings.ReadAsync(PersonalConfig), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rollback_of_a_system_app_goes_through_the_two_steps()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: approval);
        harness.Publish(Trusted, "1.0.0");
        await gateway.InstallPackageAsync(Trusted, HackPackageScope.System);
        harness.Publish(Trusted, "1.1.0", "1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.1.0", HackPackageScope.System);
        int calls = agent.Calls;

        HackPackageInstallReport report = await gateway.RollbackPackageAsync(AppPackageTests.AppId);

        Assert.True(report.SystemWide);
        Assert.Equal(calls + 1, agent.Calls);
        AppInstallApprovalRequest request = approval.Requests[^1];
        Assert.Equal(AppInstallApprovalOperation.Rollback, request.Operation);
        Assert.Equal(("1.0.0", "1.1.0"), (request.Version, request.PreviousVersion));
        Assert.Contains("org.kazo.sample=github:Kazo-ca/HackerOs-App-Sample@1.0.0", await harness.Settings.ReadAsync(SystemConfig), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_kept_version_a_rollback_is_refused()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        harness.Publish(Trusted, "1.0.0");
        await gateway.InstallPackageAsync(Trusted);

        HackPackageException refused = await Assert.ThrowsAsync<HackPackageException>(
            () => gateway.RollbackPackageAsync(AppPackageTests.AppId).AsTask());

        Assert.Equal("apps.package.no-previous", refused.Code);
        Assert.Null(Assert.Single(await gateway.GetInstalledPackagesAsync()).PreviousVersion);
    }

    [Fact]
    public async Task Only_one_previous_version_is_kept()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        harness.Publish(Trusted, "1.2.0", "1.1.0", "1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.1.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.2.0");

        Assert.Null(await harness.Settings.StatAsync($"{PersonalFolder}/1.0.0"));
        Assert.NotNull(await harness.Settings.StatAsync($"{PersonalFolder}/1.1.0/manifest.json"));
        Assert.NotNull(await harness.Settings.StatAsync($"{PersonalFolder}/1.2.0/manifest.json"));
    }

    [Fact]
    public async Task Repair_undoes_an_install_interrupted_before_its_commit()
    {
        Harness harness = await Harness.CreateAsync();
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: approval);
        harness.Publish(Trusted, "1.0.0");
        await gateway.InstallPackageAsync(Trusted, HackPackageScope.System);
        // The page closed while 1.1.0 was being written: its journal and half a folder are left.
        await harness.Settings.WriteAsRootAsync(
            $"{Folder}/{AppPackageInstaller.TransactionFileName}",
            "{\"operation\":\"install\",\"version\":\"1.1.0\",\"previous\":\"1.0.0\",\"startedAt\":\"2026-10-31T00:00:00Z\"}");
        await harness.Settings.WriteAsRootAsync($"{Folder}/1.1.0{AppPackageInstaller.PartialSuffix}/manifest.json", "{");

        IReadOnlyList<string> repaired = await gateway.RepairPackagesAsync(system: true);

        Assert.Contains(repaired, line => line.Contains("undone", StringComparison.Ordinal));
        Assert.Null(await harness.Settings.StatAsync($"{Folder}/{AppPackageInstaller.TransactionFileName}"));
        Assert.Null(await harness.Settings.StatAsync($"{Folder}/1.1.0{AppPackageInstaller.PartialSuffix}"));
        Assert.NotNull(await harness.Settings.StatAsync($"{Folder}/1.0.0/manifest.json"));
        Assert.Equal("1.0.0", Assert.Single(await gateway.GetInstalledPackagesAsync()).Version);
        Assert.Empty(await gateway.RepairPackagesAsync(system: true));
    }

    [Fact]
    public async Task Repair_finishes_an_install_interrupted_after_its_commit()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        harness.Publish(Trusted, "1.1.0", "1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.1.0");
        // [installed] already names 1.1.0, but the clean-up of an older folder did not run.
        await harness.Settings.WriteAsRootAsync($"{PersonalFolder}/0.9.0/manifest.json", "{}");
        await harness.Settings.WriteAsRootAsync(
            $"{PersonalFolder}/{AppPackageInstaller.TransactionFileName}",
            "{\"operation\":\"install\",\"version\":\"1.1.0\",\"previous\":\"1.0.0\",\"startedAt\":\"2026-10-31T00:00:00Z\"}");

        IReadOnlyList<string> repaired = await gateway.RepairPackagesAsync(system: false);

        Assert.Contains(repaired, line => line.Contains("finished", StringComparison.Ordinal));
        Assert.Null(await harness.Settings.StatAsync($"{PersonalFolder}/0.9.0"));
        Assert.NotNull(await harness.Settings.StatAsync($"{PersonalFolder}/1.0.0/manifest.json"));
        Assert.Null(await harness.Settings.StatAsync($"{PersonalFolder}/{AppPackageInstaller.TransactionFileName}"));
    }

    [Fact]
    public async Task A_version_older_than_the_minimum_upgradable_one_must_be_reinstalled()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        harness.Publish(Trusted, "1.0.0");
        await gateway.InstallPackageAsync(Trusted);
        harness.PublishSigned(Trusted, "2.0.0", key: null, minimumUpgradableVersion: "1.5.0");

        HackPackageException refused = await Assert.ThrowsAsync<HackPackageException>(
            () => gateway.InstallPackageAsync($"{Trusted}@2.0.0").AsTask());

        Assert.Equal("apps.package.reinstall-required", refused.Code);
        Assert.Equal("1.0.0", Assert.Single(await gateway.GetInstalledPackagesAsync()).Version);
        Assert.Null(await harness.Settings.StatAsync($"{PersonalFolder}/2.0.0"));
    }

    [Fact]
    public async Task A_source_with_a_signing_key_accepts_only_packages_it_signed()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Harness harness = await Harness.CreateAsync();
        await harness.AddSigningKeyAsync("github.com/Kazo-ca/HackerOs-App-Sample", key);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        harness.PublishSigned(Trusted, "1.0.0", key: null);
        HackPackageException unsigned = await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Trusted).AsTask());
        harness.PublishSigned(Trusted, "1.0.0", other);
        HackPackageException forged = await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Trusted).AsTask());
        harness.PublishSigned(Trusted, "1.0.0", key);
        HackPackageInfo info = await gateway.GetPackageInfoAsync(Trusted);
        HackPackageInstallReport report = await gateway.InstallPackageAsync(Trusted);

        Assert.Equal("apps.package.unsigned", unsigned.Code);
        Assert.Equal("apps.package.bad-signature", forged.Code);
        Assert.True(info.SignatureRequired);
        Assert.True(report.Signed);
    }

    [Fact]
    public async Task A_source_without_a_key_installs_on_its_sha256_alone()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        harness.Publish(Trusted, "1.0.0");

        HackPackageInfo info = await gateway.GetPackageInfoAsync(Trusted);
        HackPackageInstallReport report = await gateway.InstallPackageAsync(Trusted);

        Assert.False(info.SignatureRequired);
        Assert.False(report.Signed);
    }

    [Fact]
    public async Task A_removal_deletes_every_kept_version()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        harness.Publish(Trusted, "1.1.0", "1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.0.0");
        await gateway.InstallPackageAsync($"{Trusted}@1.1.0");

        await gateway.RemovePackageAsync(AppPackageTests.AppId);

        Assert.Null(await harness.Settings.StatAsync(PersonalFolder));
    }

    private sealed partial class Harness
    {
        /// <summary>Lists a key in <c>[signing-keys]</c>, as an administrator would with <c>sudo nano</c>.</summary>
        internal async Task AddSigningKeyAsync(string trustKey, ECDsa key)
        {
            string path = AppSourcesConfigDocument.MachinePath.Value;
            string content = await Settings.ReadAsync(path) ?? AppSourcesConfigDocument.BuildDefaultContent(AppPackageDefaults.Standard);
            await Settings.WriteAsRootAsync(
                path, $"{content.TrimEnd('\n')}\n\n[{AppSourcesConfigDocument.SigningKeysSection}]\n{trustKey}={Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}\n");
        }

        /// <summary>Publishes one version, signed by <paramref name="key"/> when one is given.</summary>
        internal void PublishSigned(string repository, string version, ECDsa? key, string? minimumUpgradableVersion = null)
        {
            string root = $"https://raw.githubusercontent.com/{repository}/hackeros-packages/";
            Dictionary<string, byte[]> files = AppPackageTests.SampleFiles(version);
            files["lib/Kazo.Sample.dll"] = File.ReadAllBytes(typeof(AppManifest).Assembly.Location);
            if (minimumUpgradableVersion is not null)
            {
                string manifest = Encoding.UTF8.GetString(files["manifest.json"]);
                files["manifest.json"] = Encoding.UTF8.GetBytes(manifest[..^1]
                    + ",\"update\":{\"migrationIds\":[],\"minimumUpgradableVersion\":\"" + minimumUpgradableVersion + "\"}}");
            }

            byte[] package = AppPackageTests.Build(files, version: version);
            string path = $"{version}/org.kazo.sample-{version}.hackapp";
            _downloader.Files[new Uri(root + path)] = package;
            string signature = key is null
                ? string.Empty
                : $",\"signature\":\"{Convert.ToBase64String(key.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))}\"";
            _downloader.Files[new Uri(root + "index.json")] = Encoding.UTF8.GetBytes(
                $"{{\"appId\":\"{AppPackageTests.AppId}\",\"versions\":[{{\"version\":\"{version}\",\"path\":\"{path}\",\"sha256\":\"{AppPackage.Sha256(package)}\"{signature}}}]}}");
        }
    }
}
