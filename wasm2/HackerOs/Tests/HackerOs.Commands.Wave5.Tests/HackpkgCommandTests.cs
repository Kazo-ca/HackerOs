using System.Collections.Immutable;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Apps;
using HackerOs.Commands.Hackpkg;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;
using Xunit;

namespace HackerOs.Commands.Wave5.Tests;

/// <summary><c>hackpkg</c> (ADR 0182 §4, ADR 0189 §6): arguments and the <see cref="HackAppPackages"/> calls.</summary>
public sealed class HackpkgCommandTests
{
    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("install")]
    [InlineData("install", "a/b", "c/d")]
    [InlineData("install", "a/b", "--allow-untrusted")]
    [InlineData("install", "a/b", "--user", "--system")]
    [InlineData("list", "--user")]
    [InlineData("remove", "--force", "x")]
    [InlineData("sources", "add")]
    [InlineData("sources", "remove", "x", "--trusted")]
    [InlineData("policy", "block", "planet", "x")]
    [InlineData("policy", "block", "app")]
    public async Task Bad_arguments_print_the_usage_and_exit_2_without_calling_the_os(params string[] args)
    {
        FakePackages packages = new();

        (int exit, _, string stderr) = await RunAsync(packages, args);

        Assert.Equal(2, exit);
        Assert.Contains("usage: hackpkg install <repo>[@version] [--user|--system]", stderr, StringComparison.Ordinal);
        Assert.Empty(packages.Calls);
    }

    [Theory]
    [InlineData(new string[0], "Auto")]
    [InlineData(new[] { "--user" }, "User")]
    [InlineData(new[] { "--system" }, "System")]
    public async Task Install_passes_the_scope_and_reports_the_version_the_capabilities_and_the_restart(string[] options, string scope)
    {
        FakePackages packages = new();

        (int exit, string stdout, string stderr) = await RunAsync(packages, ["install", "Kazo-ca/HackerOs-App-Webamp", .. options]);

        Assert.Equal(0, exit);
        Assert.Equal([$"install Kazo-ca/HackerOs-App-Webamp scope={scope}"], packages.Calls);
        Assert.Contains("Webamp 1.0.0 installed from github:Kazo-ca/HackerOs-App-Webamp@1.0.0", stdout, StringComparison.Ordinal);
        Assert.Contains(scope == "System" ? "installed for every user" : "installed for you only", stdout, StringComparison.Ordinal);
        Assert.Contains("network.real.access (!)", stdout, StringComparison.Ordinal);
        Assert.Contains("restart HackerOS to finish", stdout, StringComparison.Ordinal);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task A_refused_package_prints_its_reason_and_exits_1()
    {
        FakePackages packages = new() { Refusal = new HackPackageException("apps.package.blocked", "org.kazo.webamp is blocked") };

        (int exit, _, string stderr) = await RunAsync(packages, "install", "someone/app", "--user");

        Assert.Equal(1, exit);
        Assert.Contains("hackpkg: org.kazo.webamp is blocked", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_shows_the_scope_and_marks_the_apps_waiting_for_a_restart()
    {
        (int exit, string stdout, _) = await RunAsync(new FakePackages(), "list");

        Assert.Equal(0, exit);
        Assert.Contains("org.kazo.webamp  1.0.0  system  github:Kazo-ca/HackerOs-App-Webamp", stdout, StringComparison.Ordinal);
        Assert.Contains("org.kazo.irc     0.2.0  user  github:Kazo-ca/HackerOs-App-IRC (restart pending)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_installs_only_newer_versions_where_the_copy_is()
    {
        FakePackages packages = new();

        (int exit, string stdout, _) = await RunAsync(packages, "update");

        Assert.Equal(0, exit);
        Assert.Contains("install github:Kazo-ca/HackerOs-App-Webamp@1.1.0 scope=System", packages.Calls);
        Assert.DoesNotContain(packages.Calls, call => call.StartsWith("install github:Kazo-ca/HackerOs-App-IRC", StringComparison.Ordinal));
        Assert.Contains("org.kazo.irc 0.2.0 is up to date", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_passes_the_scope()
    {
        FakePackages packages = new();

        (int exit, string stdout, _) = await RunAsync(packages, "remove", "org.kazo.irc", "--user");

        Assert.Equal(0, exit);
        Assert.Equal(["remove org.kazo.irc scope=User"], packages.Calls);
        Assert.Contains("org.kazo.irc 0.2.0 removed", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sources_lists_catalogs_trusted_and_untrusted_sources()
    {
        (int exit, string stdout, _) = await RunAsync(new FakePackages(), "sources");

        Assert.Equal(0, exit);
        Assert.Contains("catalog: official https://example.org/catalog.json (verified (built in))", stdout, StringComparison.Ordinal);
        Assert.Contains("trusted: catalog:official (built in), github.com/Kazo-ca (built in)", stdout, StringComparison.Ordinal);
        Assert.Contains("untrusted: github.com/someone", stdout, StringComparison.Ordinal);
        Assert.Contains("updates: every 6 h", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sources_add_and_remove_call_the_os()
    {
        FakePackages packages = new();

        (int added, string stdout, _) = await RunAsync(packages, "sources", "add", "github:someone", "--trusted");
        (int removed, _, _) = await RunAsync(packages, "sources", "remove", "github:someone");

        Assert.Equal((0, 0), (added, removed));
        Assert.Equal(["sources add github:someone trusted=True", "sources remove github:someone"], packages.Calls);
        Assert.Contains("source github:someone added and trusted", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policy_lists_and_edits_the_block_list()
    {
        FakePackages packages = new();

        (int listed, string list, _) = await RunAsync(packages, "policy");
        (int blocked, _, _) = await RunAsync(packages, "policy", "block", "category", "social");
        (int unblocked, _, _) = await RunAsync(packages, "policy", "unblock", "capability", "network.real.access");

        Assert.Equal((0, 0, 0), (listed, blocked, unblocked));
        Assert.Contains("blocked categories:   social", list, StringComparison.Ordinal);
        Assert.Equal(["policy Category social True", "policy Capability network.real.access False"], packages.Calls);
    }

    [Fact]
    public async Task A_host_without_packages_says_so()
    {
        (int exit, _, string stderr) = await RunAsync(new FakePackages { Supported = false }, "sources");

        Assert.Equal(1, exit);
        Assert.Contains("cannot install apps from a repository", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(FakePackages packages, params string[] args)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new HackpkgApp());
        PackagesContext app = new(manifest, packages);
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app: app,
            arguments: args.ToImmutableArray(),
            standardInput: TextReader.Null,
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/root",
            environment: ImmutableDictionary<string, string>.Empty);
        using IDisposable scope = HackExecutionContext.Enter(app);
        int exit = await new HackpkgCommand(manifest).ExecuteAsync(context, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private sealed class FakePackages : IAppManagementGateway
    {
        public bool Supported { get; init; } = true;

        public HackPackageException? Refusal { get; init; }

        public List<string> Calls { get; } = [];

        public ValueTask<IReadOnlyList<HackAppInstallInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> UninstallAsync(string appId, HackInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> ReinstallAsync(string appId, HackInstallScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<HackPackageSources> GetPackageSourcesAsync(CancellationToken cancellationToken = default) =>
            Supported ? ValueTask.FromResult(Sources()) : throw new NotSupportedException();

        public ValueTask<IReadOnlyList<HackInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<HackInstalledPackage>>(
            [
                new("org.kazo.webamp", "github:Kazo-ca/HackerOs-App-Webamp", "1.0.0", Loaded: true),
                new("org.kazo.irc", "github:Kazo-ca/HackerOs-App-IRC", "0.2.0", Loaded: false) { SystemWide = false }
            ]);

        public ValueTask<HackPackageInfo> GetPackageInfoAsync(string source, CancellationToken cancellationToken = default)
        {
            Calls.Add($"info {source}");
            bool webamp = source.Contains("Webamp", StringComparison.Ordinal);
            return ValueTask.FromResult(webamp
                ? new HackPackageInfo(source, "org.kazo.webamp", ["1.1.0", "1.0.0"], "1.1.0", true, "1.0.0")
                : new HackPackageInfo(source, "org.kazo.irc", ["0.2.0"], "0.2.0", true, "0.2.0"));
        }

        public ValueTask<HackPackageInstallReport> InstallPackageAsync(
            string source, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
        {
            Calls.Add($"install {source} scope={scope}");
            if (Refusal is not null)
            {
                throw Refusal;
            }

            string version = source.EndsWith("@1.1.0", StringComparison.Ordinal) ? "1.1.0" : "1.0.0";
            return ValueTask.FromResult(new HackPackageInstallReport(
                "org.kazo.webamp", "Webamp", version, $"github:Kazo-ca/HackerOs-App-Webamp@{version}",
                version == "1.0.0" ? null : "1.0.0", [AppCapabilities.NetworkRealAccess], Trusted: true)
            {
                SystemWide = scope == HackPackageScope.System,
            });
        }

        public ValueTask<HackInstalledPackage> RemovePackageAsync(
            string appId, HackPackageScope scope = HackPackageScope.Auto, CancellationToken cancellationToken = default)
        {
            Calls.Add($"remove {appId} scope={scope}");
            return ValueTask.FromResult(new HackInstalledPackage(appId, "github:Kazo-ca/HackerOs-App-IRC", "0.2.0", Loaded: true));
        }

        public ValueTask<HackPackageSources> AddPackageSourceAsync(string source, bool trusted, CancellationToken cancellationToken = default)
        {
            Calls.Add($"sources add {source} trusted={trusted}");
            return ValueTask.FromResult(Sources());
        }

        public ValueTask<HackPackageSources> RemovePackageSourceAsync(string source, CancellationToken cancellationToken = default)
        {
            Calls.Add($"sources remove {source}");
            return ValueTask.FromResult(Sources());
        }

        public ValueTask<HackPackagePolicy> GetPackagePolicyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new HackPackagePolicy([], ["social"], [], []));

        public ValueTask<HackPackagePolicy> SetPackagePolicyAsync(
            HackPackageBlockKind kind, string value, bool blocked, CancellationToken cancellationToken = default)
        {
            Calls.Add($"policy {kind} {value} {blocked}");
            return ValueTask.FromResult(new HackPackagePolicy([], [], [], []));
        }

        private static HackPackageSources Sources() => new("raw.githubusercontent.com", ["catalog:official", "github.com/Kazo-ca"], [])
        {
            Catalogs = [new HackPackageCatalog("official", "https://example.org/catalog.json", HackPackageTrust.Verified, BuiltIn: true)],
            BuiltInSources = ["catalog:official", "github.com/Kazo-ca"],
            UntrustedSources = ["github.com/someone"],
            UpdateIntervalHours = 6,
        };
    }

    private sealed class PackagesContext(AppManifest manifest, IAppManagementGateway packages) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.Administrator;
        public IAppManagementGateway AppManagement => packages;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(Manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }
}
