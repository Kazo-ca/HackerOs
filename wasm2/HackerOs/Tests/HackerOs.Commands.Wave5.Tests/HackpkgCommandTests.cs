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

/// <summary><c>hackpkg</c> (ADR 0182, section 4): arguments and the <see cref="HackAppPackages"/> calls.</summary>
public sealed class HackpkgCommandTests
{
    [Theory]
    [InlineData]
    [InlineData("bogus")]
    [InlineData("install")]
    [InlineData("install", "a/b", "c/d")]
    [InlineData("list", "--allow-untrusted")]
    [InlineData("remove", "--force", "x")]
    public async Task Bad_arguments_print_the_usage_and_exit_2_without_calling_the_os(params string[] args)
    {
        FakePackages packages = new();

        (int exit, _, string stderr) = await RunAsync(packages, args);

        Assert.Equal(2, exit);
        Assert.Contains("usage: hackpkg install <repo>[@version]", stderr, StringComparison.Ordinal);
        Assert.Empty(packages.Calls);
    }

    [Fact]
    public async Task Install_reports_the_version_the_capabilities_and_the_restart()
    {
        FakePackages packages = new();

        (int exit, string stdout, string stderr) = await RunAsync(packages, "install", "Kazo-ca/HackerOs-App-Webamp", "--allow-untrusted");

        Assert.Equal(0, exit);
        Assert.Equal(["install Kazo-ca/HackerOs-App-Webamp untrusted=True"], packages.Calls);
        Assert.Contains("Webamp 1.0.0 installed from github:Kazo-ca/HackerOs-App-Webamp@1.0.0", stdout, StringComparison.Ordinal);
        Assert.Contains("network.real.access (!)", stdout, StringComparison.Ordinal);
        Assert.Contains("restart HackerOS to finish", stdout, StringComparison.Ordinal);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task A_refused_package_prints_its_reason_and_exits_1()
    {
        FakePackages packages = new() { Refusal = new HackPackageException("apps.package.untrusted", "github.com/someone is not a trusted source") };

        (int exit, _, string stderr) = await RunAsync(packages, "install", "someone/app");

        Assert.Equal(1, exit);
        Assert.Contains("hackpkg: github.com/someone is not a trusted source", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_marks_the_apps_waiting_for_a_restart()
    {
        (int exit, string stdout, _) = await RunAsync(new FakePackages(), "list");

        Assert.Equal(0, exit);
        Assert.Contains("org.kazo.webamp  1.0.0  github:Kazo-ca/HackerOs-App-Webamp", stdout, StringComparison.Ordinal);
        Assert.Contains("org.kazo.irc     0.2.0  github:Kazo-ca/HackerOs-App-IRC (restart pending)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_installs_only_newer_versions()
    {
        FakePackages packages = new();

        (int exit, string stdout, _) = await RunAsync(packages, "update");

        Assert.Equal(0, exit);
        Assert.Contains("install github:Kazo-ca/HackerOs-App-Webamp@1.1.0 untrusted=False", packages.Calls);
        Assert.DoesNotContain(packages.Calls, call => call.StartsWith("install github:Kazo-ca/HackerOs-App-IRC", StringComparison.Ordinal));
        Assert.Contains("org.kazo.irc 0.2.0 is up to date", stdout, StringComparison.Ordinal);
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
            Supported
                ? ValueTask.FromResult(new HackPackageSources("raw.githubusercontent.com", ["github.com/Kazo-ca"], []))
                : throw new NotSupportedException();

        public ValueTask<IReadOnlyList<HackInstalledPackage>> GetInstalledPackagesAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<HackInstalledPackage>>(
            [
                new("org.kazo.webamp", "github:Kazo-ca/HackerOs-App-Webamp", "1.0.0", Loaded: true),
                new("org.kazo.irc", "github:Kazo-ca/HackerOs-App-IRC", "0.2.0", Loaded: false)
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
            string source, bool allowUntrusted = false, CancellationToken cancellationToken = default)
        {
            Calls.Add($"install {source} untrusted={allowUntrusted}");
            if (Refusal is not null)
            {
                throw Refusal;
            }

            string version = source.EndsWith("@1.1.0", StringComparison.Ordinal) ? "1.1.0" : "1.0.0";
            return ValueTask.FromResult(new HackPackageInstallReport(
                "org.kazo.webamp", "Webamp", version, $"github:Kazo-ca/HackerOs-App-Webamp@{version}",
                version == "1.0.0" ? null : "1.0.0", [AppCapabilities.NetworkRealAccess], Trusted: true));
        }
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
