using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.AppSdk.Apps;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.Core.Apps.Packages;
using HackerOs.Platform.Core.Elevation;
using HackerOs.Platform.Core.Execution;
using HackerOs.Platform.Core.Tests.Apps.Packages;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Tests.Execution;

/// <summary>
/// Installing apps from their GitHub repository through <c>HackAppPackages</c>' gateway (ADR 0182
/// §3 to §5), for the session user or for the system, with ADR 0189's two-step approval.
/// </summary>
public sealed class AppPackageGatewayTests
{
    private const string Trusted = "Kazo-ca/HackerOs-App-Sample";
    private const string Untrusted = "someone/sample";
    private const string Folder = "/usr/lib/hackeros/apps/org.kazo.sample";
    private const string PersonalFolder = "/home/alice/.local/share/hackeros/apps/org.kazo.sample";
    private const string PersonalConfig = "/home/alice/.config/hackeros/installed-apps.config";

    [Fact]
    public async Task A_trusted_app_is_installed_for_the_user_without_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);

        HackPackageInstallReport report = await (await harness.CreateGatewayAsync(agent, approval: approval)).InstallPackageAsync(Trusted);

        Assert.Equal(0, agent.Calls);
        Assert.Empty(approval.Requests);
        Assert.False(report.SystemWide);
        Assert.Equal(HackPackageTrust.Trusted, report.Trust);
        Assert.Equal("hello", await harness.Settings.ReadAsync($"{PersonalFolder}/1.0.0/wwwroot/data/x.txt"));
        Assert.Contains(
            "org.kazo.sample=github:Kazo-ca/HackerOs-App-Sample@1.0.0",
            await harness.Settings.ReadAsync(PersonalConfig),
            StringComparison.Ordinal);
        Assert.Null(await harness.Settings.StatAsync(Folder));
        HackInstalledPackage installed = Assert.Single(await (await harness.CreateGatewayAsync(agent)).GetInstalledPackagesAsync());
        Assert.False(installed.SystemWide);
        Assert.False(installed.Loaded);
    }

    [Fact]
    public async Task A_system_install_asks_a_fresh_sign_in_then_the_approval_window_even_under_sudo()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: approval, isRootElevated: true);

        HackPackageInstallReport report = await gateway.InstallPackageAsync(Trusted, HackPackageScope.System);

        Assert.Equal(1, agent.Calls);
        Assert.False(agent.LastRequest!.IsRootElevated);
        Assert.Contains("Sample 1.0.0", agent.LastRequest.Operation, StringComparison.Ordinal);
        AppInstallApprovalRequest shown = Assert.Single(approval.Requests);
        Assert.Equal(("org.kazo.sample", "1.0.0", "admin", true), (shown.AppId, shown.Version, shown.AdministratorLogin, shown.SystemWide));
        Assert.Equal(AppSourceTrust.Trusted, shown.Trust);
        Assert.Equal(AppInstallApprovalOperation.Install, shown.Operation);
        Assert.True(shown.SizeBytes > 0);
        Assert.True(report.SystemWide);
        Assert.True(report.RestartRequired);
        Assert.Equal(0b110_100_100, (await harness.Settings.StatAsync($"{Folder}/1.0.0/lib/Kazo.Sample.dll"))!.Permissions.Mode);
        Assert.Contains(
            "org.kazo.sample=github:Kazo-ca/HackerOs-App-Sample@1.0.0",
            await harness.Settings.ReadAsync("/etc/hackeros/apps.config"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_untrusted_source_always_goes_through_the_two_steps()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Untrusted, "1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);

        HackPackageInstallReport forSystem = await (await harness.CreateGatewayAsync(agent, approval: approval)).InstallPackageAsync(Untrusted);

        Assert.True(forSystem.SystemWide);
        Assert.Equal(HackPackageTrust.Untrusted, forSystem.Trust);
        Assert.False(forSystem.Trusted);
        Assert.Contains("untrusted", agent.LastRequest!.Operation, StringComparison.Ordinal);
        Assert.Equal(AppSourceTrust.Untrusted, Assert.Single(approval.Requests).Trust);
    }

    [Fact]
    public async Task An_untrusted_source_for_the_user_alone_still_asks_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Untrusted, "1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);

        HackPackageInstallReport report = await (await harness.CreateGatewayAsync(agent, approval: approval))
            .InstallPackageAsync(Untrusted, HackPackageScope.User);

        Assert.False(report.SystemWide);
        Assert.Equal(1, agent.Calls);
        Assert.Contains("alice", agent.LastRequest!.Operation, StringComparison.Ordinal);
        Assert.False(Assert.Single(approval.Requests).SystemWide);
        Assert.NotNull(await harness.Settings.StatAsync($"{PersonalFolder}/1.0.0/manifest.json"));
    }

    [Theory]
    [InlineData(false, AppInstallApprovalOutcome.Approved)]
    [InlineData(true, AppInstallApprovalOutcome.Refused)]
    [InlineData(true, AppInstallApprovalOutcome.Unavailable)]
    public async Task A_failed_sign_in_or_an_approval_not_given_writes_nothing(bool signsIn, AppInstallApprovalOutcome outcome)
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        FakeApproval approval = new(outcome);

        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(
            async () => await (await harness.CreateGatewayAsync(new CountingAgent(signsIn), approval: approval))
                .InstallPackageAsync(Trusted, HackPackageScope.System));

        Assert.Equal(signsIn ? 1 : 0, approval.Requests.Count);
        Assert.Null(await harness.Settings.StatAsync(Folder));
        Assert.Empty(await (await harness.CreateGatewayAsync(new CountingAgent(succeed: true))).GetInstalledPackagesAsync());
    }

    [Fact]
    public async Task A_host_without_an_approval_window_cannot_install_for_the_system()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");

        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(
            async () => await (await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: null))
                .InstallPackageAsync(Trusted, HackPackageScope.System));

        Assert.Null(await harness.Settings.StatAsync(Folder));
    }

    [Fact]
    public async Task A_blocked_app_goes_to_the_system_install_and_cannot_be_installed_for_oneself()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        await harness.Settings.WriteAsRootAsync(
            AppPolicyConfigDocument.MachinePath.Value, "[blocked-apps]\norg.kazo.sample=true\n");
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: approval);

        HackPackageException refused = await Assert.ThrowsAsync<HackPackageException>(
            () => gateway.InstallPackageAsync(Trusted, HackPackageScope.User).AsTask());
        HackPackageInstallReport report = await gateway.InstallPackageAsync(Trusted);

        Assert.Equal("apps.package.blocked", refused.Code);
        Assert.True(report.SystemWide);
        Assert.Equal("blocked-apps: org.kazo.sample", report.BlockedBy);
        Assert.Equal(new AppBlockReason(AppBlockKind.App, "org.kazo.sample"), Assert.Single(approval.Requests).BlockReason);
    }

    [Fact]
    public async Task A_system_install_replaces_the_personal_copies()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(
            new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));
        await gateway.InstallPackageAsync(Trusted);

        HackPackageInstallReport report = await gateway.InstallPackageAsync(Trusted, HackPackageScope.System);

        Assert.Equal(1, report.ReplacedPersonalCopies);
        Assert.Null(await harness.Settings.StatAsync(PersonalFolder));
        Assert.DoesNotContain("org.kazo.sample=", await harness.Settings.ReadAsync(PersonalConfig), StringComparison.Ordinal);
        Assert.Equal(
            "apps.package.installed-for-system",
            (await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Trusted, HackPackageScope.User).AsTask())).Code);
        Assert.True(Assert.Single(await gateway.GetInstalledPackagesAsync()).SystemWide);
    }

    [Fact]
    public async Task An_update_replaces_the_previous_version_folder()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(
            new CountingAgent(succeed: true), approval: new FakeApproval(AppInstallApprovalOutcome.Approved));
        await gateway.InstallPackageAsync(Trusted, HackPackageScope.System);
        harness.Publish(Trusted, "1.1.0", "1.0.0");

        HackPackageInfo info = await gateway.GetPackageInfoAsync(Trusted);
        HackPackageInstallReport report = await gateway.InstallPackageAsync($"{Trusted}@{info.Latest}", HackPackageScope.System);

        Assert.Equal(["1.1.0", "1.0.0"], info.Versions);
        Assert.Equal("1.0.0", info.InstalledVersion);
        Assert.True(info.InstalledSystemWide);
        Assert.Equal("1.0.0", report.PreviousVersion);
        Assert.Null(await harness.Settings.StatAsync($"{Folder}/1.0.0"));
        Assert.NotNull(await harness.Settings.StatAsync($"{Folder}/1.1.0/manifest.json"));
        Assert.Equal(
            "apps.package.already-installed",
            (await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Trusted, HackPackageScope.System).AsTask())).Code);
    }

    [Fact]
    public async Task A_personal_copy_is_removed_without_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent);
        await gateway.InstallPackageAsync(Trusted);

        HackInstalledPackage removed = await gateway.RemovePackageAsync("org.kazo.sample");

        Assert.False(removed.SystemWide);
        Assert.Equal(0, agent.Calls);
        Assert.Null(await harness.Settings.StatAsync(PersonalFolder));
        Assert.DoesNotContain("org.kazo.sample=", await harness.Settings.ReadAsync(PersonalConfig), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_system_removal_goes_through_the_two_steps()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), approval: approval);
        await gateway.InstallPackageAsync(Trusted, HackPackageScope.System);

        HackInstalledPackage removed = await gateway.RemovePackageAsync("org.kazo.sample");

        Assert.True(removed.SystemWide);
        Assert.Equal(AppInstallApprovalOperation.Remove, approval.Requests[^1].Operation);
        Assert.Null(await harness.Settings.StatAsync(Folder));
        Assert.DoesNotContain("org.kazo.sample=", await harness.Settings.ReadAsync("/etc/hackeros/apps.config"), StringComparison.Ordinal);
        Assert.Equal(
            "apps.package.not-installed",
            (await Assert.ThrowsAsync<HackPackageException>(() => gateway.RemovePackageAsync("org.kazo.sample").AsTask())).Code);
    }

    [Theory]
    [InlineData("bad-hash", "apps.package.hash-mismatch")]
    [InlineData("built-in", "apps.package.built-in")]
    [InlineData("trimmed", "apps.package.missing-api")]
    [InlineData("no-index", "apps.package.no-index")]
    public async Task A_package_the_os_cannot_take_is_refused_before_the_prompt(string problem, string code)
    {
        Harness harness = await Harness.CreateAsync(builtInSample: problem == "built-in", runtimeHasEverything: problem != "trimmed");
        if (problem != "no-index")
        {
            harness.Publish(Trusted, "1.0.0", corruptIndexHash: problem == "bad-hash");
        }

        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: new FakeApproval(AppInstallApprovalOutcome.Approved));

        HackPackageException refused = await Assert.ThrowsAsync<HackPackageException>(
            () => gateway.InstallPackageAsync(Trusted, HackPackageScope.System).AsTask());

        Assert.Equal(code, refused.Code);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task The_default_sources_trust_the_official_catalog_and_kazo_ca()
    {
        Harness harness = await Harness.CreateAsync();

        HackPackageSources sources = await (await harness.CreateGatewayAsync(new CountingAgent(succeed: false))).GetPackageSourcesAsync();

        Assert.Equal("raw.githubusercontent.com", sources.GitHubHost);
        Assert.Equal(["catalog:official", "github.com/Kazo-ca"], sources.TrustedSources);
        Assert.Equal(sources.TrustedSources, sources.BuiltInSources);
        HackPackageCatalog official = Assert.Single(sources.Catalogs);
        Assert.Equal(("official", HackPackageTrust.Verified, true), (official.Name, official.Trust, official.BuiltIn));
        Assert.Equal(AppPackageDefaults.DefaultUpdateIntervalHours, sources.UpdateIntervalHours);
    }

    [Fact]
    public async Task An_administrator_adds_and_removes_sources_with_one_sign_in()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Untrusted, "1.0.0");
        CountingAgent agent = new(succeed: true);
        FakeApproval approval = new(AppInstallApprovalOutcome.Approved);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent, approval: approval);

        HackPackageSources added = await gateway.AddPackageSourceAsync("github:someone", trusted: true);
        HackPackageInstallReport report = await gateway.InstallPackageAsync(Untrusted);

        Assert.Equal(1, agent.Calls);
        Assert.Equal("apps.sources.add", agent.LastRequest!.Action);
        Assert.Contains("github.com/someone", added.TrustedSources);
        Assert.False(report.SystemWide);
        Assert.Empty(approval.Requests);

        HackPackageSources removed = await gateway.RemovePackageSourceAsync("github:someone");
        Assert.DoesNotContain("github.com/someone", removed.TrustedSources);
        Assert.Equal(
            "apps.source.not-found",
            (await Assert.ThrowsAsync<HackPackageException>(() => gateway.RemovePackageSourceAsync("github:someone").AsTask())).Code);
        Assert.Equal(
            "apps.source.invalid",
            (await Assert.ThrowsAsync<HackPackageException>(() => gateway.AddPackageSourceAsync("not a source", trusted: false).AsTask())).Code);
    }

    [Fact]
    public async Task An_untrusted_source_is_listed_apart()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));

        HackPackageSources sources = await gateway.AddPackageSourceAsync("someone/sample", trusted: false);

        Assert.Contains("github.com/someone/sample", sources.UntrustedSources);
        Assert.DoesNotContain("github.com/someone/sample", sources.TrustedSources);
    }

    [Fact]
    public async Task The_block_list_is_read_and_edited_by_an_administrator()
    {
        Harness harness = await Harness.CreateAsync();
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent);

        Assert.Empty((await gateway.GetPackagePolicyAsync()).BlockedApps);
        HackPackagePolicy blocked = await gateway.SetPackagePolicyAsync(HackPackageBlockKind.Category, "social", blocked: true);
        HackPackagePolicy unblocked = await gateway.SetPackagePolicyAsync(HackPackageBlockKind.Category, "social", blocked: false);

        Assert.Equal(["social"], blocked.BlockedCategories);
        Assert.Empty(unblocked.BlockedCategories);
        Assert.Equal(2, agent.Calls);
        Assert.Equal(
            "apps.policy.invalid",
            (await Assert.ThrowsAsync<HackPackageException>(
                () => gateway.SetPackagePolicyAsync(HackPackageBlockKind.App, "a b", blocked: true).AsTask())).Code);
    }

    [Fact]
    public async Task Every_package_call_requires_apps_manage()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), capabilities: []);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetInstalledPackagesAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.InstallPackageAsync(Trusted).AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.AddPackageSourceAsync(Trusted, trusted: true).AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(
            () => gateway.SetPackagePolicyAsync(HackPackageBlockKind.App, "x", blocked: true).AsTask());
    }

    /// <summary>The approval window of step 2: records what it showed and ends with a fixed outcome.</summary>
    private sealed class FakeApproval(AppInstallApprovalOutcome outcome) : IAppInstallApprovalPrompt
    {
        public List<AppInstallApprovalRequest> Requests { get; } = [];

        public Task<AppInstallApprovalOutcome> RequestAsync(AppInstallApprovalRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(outcome);
        }
    }

    /// <summary>Records requests and answers with a fixed outcome.</summary>
    private sealed class CountingAgent(bool succeed) : IAdministratorAuthenticationAgent
    {
        public int Calls { get; private set; }

        public AdministratorAuthenticationRequest? LastRequest { get; private set; }

        public Task<AdministratorAuthenticationResult> AuthenticateAsync(
            AdministratorAuthenticationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(succeed
                ? new AdministratorAuthenticationResult(true, "admin", null)
                : AdministratorAuthenticationResult.Failed("cancelled"));
        }
    }

    /// <summary>Grants exactly the listed capabilities (the gateways only call <see cref="Require"/>).</summary>
    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }

    /// <summary>The runtime as the check sees it: everything present, or nothing.</summary>
    private sealed class FixedRuntime(bool present) : IRuntimeApiSurface
    {
        public bool HasAssembly(string assemblyName) => present;

        public bool HasType(string assemblyName, string typeName) => present;

        public bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, int parameterCount) => present;

        public bool HasField(string assemblyName, string typeName, string fieldName) => present;
    }

    /// <summary>Serves the files of the <c>hackeros-packages</c> branches the tests publish.</summary>
    private sealed class FakeDownloader : IAppPackageDownloader
    {
        public Dictionary<Uri, byte[]> Files { get; } = [];

        public List<Uri> Requested { get; } = [];

        public ValueTask<byte[]?> DownloadAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken = default)
        {
            Requested.Add(uri);
            return ValueTask.FromResult(Files.TryGetValue(uri, out byte[]? bytes) ? bytes : null);
        }
    }

    private sealed class Harness
    {
        private readonly FakeDownloader _downloader = new();
        private readonly AppPackageInstaller _installer;

        private Harness(SettingsFilesHarness settings, bool builtInSample, bool runtimeHasEverything)
        {
            Settings = settings;
            List<AppManifest> apps = [Window("org.test.term", "Term")];
            if (builtInSample)
            {
                apps.Add(Window(AppPackageTests.AppId, "Sample"));
            }

            AppCatalogBuildResult built = AppCatalog.Build(apps, ["org.test.term"]);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(error => error.Message)));
            Catalog = built.Catalog!;
            _installer = new AppPackageInstaller(_downloader, new FixedRuntime(runtimeHasEverything), SemanticVersion.Parse("2.1.0"));
        }

        internal SettingsFilesHarness Settings { get; }

        internal AppCatalog Catalog { get; }

        internal IReadOnlyList<Uri> Downloads => _downloader.Requested;

        internal static async Task<Harness> CreateAsync(bool builtInSample = false, bool runtimeHasEverything = true)
        {
            Harness harness = new(new SettingsFilesHarness(), builtInSample, runtimeHasEverything);
            await harness.Settings.BootAsync();
            return harness;
        }

        internal async Task<AppManagementGateway> CreateGatewayAsync(
            IAdministratorAuthenticationAgent agent,
            string[]? capabilities = null,
            IAppInstallApprovalPrompt? approval = null,
            bool isRootElevated = false)
        {
            AuthenticatedPrincipal alice = Settings.Session.CurrentPrincipal ?? await Settings.LoginAsync(Settings.Alice);
            return new AppManagementGateway(
                Catalog, () => throw new NotSupportedException(), () => agent,
                new FixedCapabilities(capabilities ?? [AppCapabilities.AppsManage]), Catalog.Manifests["org.test.term"],
                alice, isRootElevated, Settings.FileSystem, Settings.Clock, packages: () => _installer, approval: () => approval);
        }

        /// <summary>Publishes the sample app on the repository's package branch, newest version first.</summary>
        internal void Publish(string repository, string latest, params string[] older) =>
            Publish(repository, latest, false, older);

        internal void Publish(string repository, string latest, bool corruptIndexHash, params string[] older)
        {
            string root = $"https://raw.githubusercontent.com/{repository}/hackeros-packages/";
            List<string> entries = [];
            foreach (string version in older.Prepend(latest))
            {
                Dictionary<string, byte[]> files = AppPackageTests.SampleFiles(version);
                // A real assembly: the reference check reads its metadata.
                files["lib/Kazo.Sample.dll"] = File.ReadAllBytes(typeof(AppManifest).Assembly.Location);
                byte[] package = AppPackageTests.Build(files, version: version);
                string path = $"{version}/org.kazo.sample-{version}.hackapp";
                _downloader.Files[new Uri(root + path)] = package;
                string sha = corruptIndexHash ? new string('0', 64) : AppPackage.Sha256(package);
                entries.Add($"{{\"version\":\"{version}\",\"path\":\"{path}\",\"sha256\":\"{sha}\"}}");
            }

            _downloader.Files[new Uri(root + "index.json")] = Encoding.UTF8.GetBytes(
                $"{{\"appId\":\"{AppPackageTests.AppId}\",\"versions\":[{string.Join(',', entries)}]}}");
        }

        private static AppManifest Window(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Version = "1.0.0",
            PublisherId = "org.test",
            Description = name,
            Kind = AppKind.Window,
            EntryPoint = new AppEntryPointManifest("HackerOs.Platform.Core.Tests", $"{id}.EntryPoint"),
            SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
            Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
            Resources = AppResourceProfileManifest.None
        };
    }
}
