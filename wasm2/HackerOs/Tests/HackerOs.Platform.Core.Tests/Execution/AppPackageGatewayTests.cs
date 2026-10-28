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

/// <summary>Installing apps from their GitHub repository through <c>HackAppPackages</c>' gateway (ADR 0182 §3 to §5).</summary>
public sealed class AppPackageGatewayTests
{
    private const string Trusted = "Kazo-ca/HackerOs-App-Sample";
    private const string Untrusted = "someone/sample";
    private const string Folder = "/usr/lib/hackeros/apps/org.kazo.sample";

    [Fact]
    public async Task Install_writes_the_package_and_its_installed_line_after_an_administrator_authenticated()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        CountingAgent agent = new(succeed: true);

        HackPackageInstallReport report = await (await harness.CreateGatewayAsync(agent)).InstallPackageAsync(Trusted);

        Assert.Equal(1, agent.Calls);
        Assert.Contains("Sample 1.0.0", agent.LastRequest!.Operation, StringComparison.Ordinal);
        Assert.Equal(("org.kazo.sample", "1.0.0", "github:Kazo-ca/HackerOs-App-Sample@1.0.0"), (report.AppId, report.Version, report.Source));
        Assert.Null(report.PreviousVersion);
        Assert.True(report.Trusted);
        Assert.True(report.RestartRequired);
        Assert.Equal(0b110_100_100, (await harness.Settings.StatAsync($"{Folder}/1.0.0/lib/Kazo.Sample.dll"))!.Permissions.Mode);
        Assert.Equal("hello", await harness.Settings.ReadAsync($"{Folder}/1.0.0/wwwroot/data/x.txt"));
        Assert.Contains(
            "org.kazo.sample=github:Kazo-ca/HackerOs-App-Sample@1.0.0",
            await harness.Settings.ReadAsync("/etc/hackeros/apps.config"),
            StringComparison.Ordinal);
        // Q5 answer C: it joins the catalog at the next start of the OS.
        HackInstalledPackage installed = Assert.Single(await (await harness.CreateGatewayAsync(agent)).GetInstalledPackagesAsync());
        Assert.False(installed.Loaded);
    }

    [Fact]
    public async Task An_untrusted_owner_is_refused_before_any_download_or_prompt_unless_allowed()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Untrusted, "1.0.0");
        CountingAgent agent = new(succeed: true);
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent);

        HackPackageException refused = await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Untrusted).AsTask());
        Assert.Equal("apps.package.untrusted", refused.Code);
        Assert.Equal(0, agent.Calls);
        Assert.Empty(harness.Downloads);

        HackPackageInstallReport report = await gateway.InstallPackageAsync(Untrusted, allowUntrusted: true);
        Assert.False(report.Trusted);
        Assert.Contains("untrusted", agent.LastRequest!.Operation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_administrator_writes_nothing()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");

        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(
            async () => await (await harness.CreateGatewayAsync(new CountingAgent(succeed: false))).InstallPackageAsync(Trusted));

        Assert.Null(await harness.Settings.StatAsync(Folder));
        Assert.Empty(await (await harness.CreateGatewayAsync(new CountingAgent(succeed: true))).GetInstalledPackagesAsync());
    }

    [Fact]
    public async Task An_update_replaces_the_previous_version_folder()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        await gateway.InstallPackageAsync(Trusted);
        harness.Publish(Trusted, "1.1.0", "1.0.0");

        HackPackageInfo info = await gateway.GetPackageInfoAsync(Trusted);
        HackPackageInstallReport report = await gateway.InstallPackageAsync($"{Trusted}@{info.Latest}");

        Assert.Equal(["1.1.0", "1.0.0"], info.Versions);
        Assert.Equal("1.0.0", info.InstalledVersion);
        Assert.Equal("1.0.0", report.PreviousVersion);
        Assert.Null(await harness.Settings.StatAsync($"{Folder}/1.0.0"));
        Assert.NotNull(await harness.Settings.StatAsync($"{Folder}/1.1.0/manifest.json"));
        Assert.Equal(
            "apps.package.already-installed",
            (await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Trusted).AsTask())).Code);
    }

    [Fact]
    public async Task Remove_deletes_the_folder_and_the_installed_line()
    {
        Harness harness = await Harness.CreateAsync();
        harness.Publish(Trusted, "1.0.0");
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true));
        await gateway.InstallPackageAsync(Trusted);

        HackInstalledPackage removed = await gateway.RemovePackageAsync("org.kazo.sample");

        Assert.Equal("1.0.0", removed.Version);
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
        AppManagementGateway gateway = await harness.CreateGatewayAsync(agent);

        HackPackageException refused = await Assert.ThrowsAsync<HackPackageException>(() => gateway.InstallPackageAsync(Trusted).AsTask());

        Assert.Equal(code, refused.Code);
        Assert.Equal(0, agent.Calls);
    }

    [Fact]
    public async Task The_seeded_sources_trust_kazo_ca_on_raw_githubusercontent()
    {
        Harness harness = await Harness.CreateAsync();

        HackPackageSources sources = await (await harness.CreateGatewayAsync(new CountingAgent(succeed: false))).GetPackageSourcesAsync();

        Assert.Equal("raw.githubusercontent.com", sources.GitHubHost);
        Assert.Equal(["github.com/Kazo-ca"], sources.TrustedOwners);
    }

    [Fact]
    public async Task Every_package_call_requires_apps_manage()
    {
        Harness harness = await Harness.CreateAsync();
        AppManagementGateway gateway = await harness.CreateGatewayAsync(new CountingAgent(succeed: true), capabilities: []);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.GetInstalledPackagesAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => gateway.InstallPackageAsync(Trusted).AsTask());
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

        internal async Task<AppManagementGateway> CreateGatewayAsync(IAdministratorAuthenticationAgent agent, string[]? capabilities = null)
        {
            AuthenticatedPrincipal alice = Settings.Session.CurrentPrincipal ?? await Settings.LoginAsync(Settings.Alice);
            return new AppManagementGateway(
                Catalog, () => throw new NotSupportedException(), () => agent,
                new FixedCapabilities(capabilities ?? [AppCapabilities.AppsManage]), Catalog.Manifests["org.test.term"],
                alice, false, Settings.FileSystem, Settings.Clock, packages: () => _installer);
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
