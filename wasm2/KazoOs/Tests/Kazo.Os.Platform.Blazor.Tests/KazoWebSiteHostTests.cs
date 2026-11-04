using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.AppSdk.Web.Blazor.Sites;
using Kazo.Os.Platform.Blazor.Sites;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>The OS side of Blazor sites (ADR 0084 §3): which app serves a page, and the renderer it starts.</summary>
public sealed class KazoWebSiteHostTests
{
    private static readonly string AssemblyName = typeof(KazoWebSiteHostTests).Assembly.GetName().Name + ".dll";

    [Fact]
    public async Task Starts_the_renderer_of_the_site_serving_the_page()
    {
        List<(string AppId, ProcessId Parent, IReadOnlyList<string> Arguments)> starts = [];
        List<(ProcessId Pid, ProcessExitReason Reason)> stops = [];
        KazoWebSiteHost host = CreateHost([SiteManifest("org.test.intranet", "http://intranet.hack/")], starts, stops);
        StubContext browser = new(SiteManifest("org.hackeros.browser", "http://x.hack/"), ProcessId.FromInt64(7));

        KazoWebSiteStart start = await host.StartAsync(new KazoWebSiteStartRequest(
            new Uri("http://intranet.hack/notes/5"), Boot("org.test.intranet"), browser));

        Assert.True(start.IsStarted, start.Error);
        IHackWebSiteProcess process = start.Process!;
        Assert.Equal("org.test.intranet", process.AppId);
        Assert.Equal(typeof(TestRoot), process.RootComponent);
        (string appId, ProcessId parent, IReadOnlyList<string> arguments) = starts.Single();
        Assert.Equal("org.test.intranet", appId);
        Assert.Equal(ProcessId.FromInt64(7), parent);
        Assert.Equal(["--type=renderer", "http://intranet.hack/notes/5"], arguments);

        await process.StopAsync(faulted: true);
        await process.StopAsync();

        Assert.Equal((process.ProcessId, ProcessExitReason.Fault), Assert.Single(stops));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task The_renderer_reports_when_the_os_ends_it()
    {
        List<(string, ProcessId, IReadOnlyList<string>)> starts = [];
        List<(ProcessId, ProcessExitReason)> stops = [];
        CancellationTokenSource ended = new();
        KazoWebSiteHost host = CreateHost([SiteManifest("org.test.intranet", "http://intranet.hack/")], starts, stops, ended.Token);
        KazoWebSiteStart start = await host.StartAsync(new KazoWebSiteStartRequest(
            new Uri("http://intranet.hack/"), Boot("org.test.intranet"), new StubContext(SiteManifest("b", "http://b.hack/"), ProcessId.FromInt64(1))));
        int exited = 0;
        start.Process!.Exited += () => exited++;

        ended.Cancel();
        await start.Process.StopAsync();

        Assert.Equal(1, exited);
        Assert.True(start.Process.HasExited);
        Assert.Empty(stops);
    }

    [Fact]
    public async Task Refuses_a_boot_manifest_naming_another_app()
    {
        KazoWebSiteHost host = CreateHost([SiteManifest("org.test.intranet", "http://intranet.hack/"), SiteManifest("org.test.wiki", "http://wiki.hack/")]);

        KazoWebSiteStart start = await host.StartAsync(new KazoWebSiteStartRequest(
            new Uri("http://intranet.hack/"), Boot("org.test.wiki"), new StubContext(SiteManifest("b", "http://b.hack/"), ProcessId.FromInt64(1))));

        Assert.False(start.IsStarted);
        Assert.Contains("org.test.wiki", start.Error);
    }

    [Fact]
    public async Task Refuses_a_page_no_installed_site_serves()
    {
        KazoWebSiteHost host = CreateHost([SiteManifest("org.test.intranet", "http://intranet.hack/")], disabled: ["org.test.intranet"]);

        KazoWebSiteStart start = await host.StartAsync(new KazoWebSiteStartRequest(
            new Uri("http://intranet.hack/"), Boot("org.test.intranet"), new StubContext(SiteManifest("b", "http://b.hack/"), ProcessId.FromInt64(1))));

        Assert.False(start.IsStarted);
        Assert.Contains("No installed site", start.Error);
    }

    [Fact]
    public async Task Refuses_a_site_without_blazor_root_or_with_a_root_that_is_not_a_component()
    {
        AppManifest mvcOnly = SiteManifest("org.test.mvc", "http://mvc.hack/") with { Web = new WebAppManifest(["http://mvc.hack/"]) };
        AppManifest notComponent = SiteManifest("org.test.bad", "http://bad.hack/", typeof(KazoWebSiteHostTests));
        KazoWebSiteHost host = CreateHost([mvcOnly, notComponent]);
        StubContext browser = new(SiteManifest("b", "http://b.hack/"), ProcessId.FromInt64(1));

        KazoWebSiteStart noRoot = await host.StartAsync(new KazoWebSiteStartRequest(new Uri("http://mvc.hack/"), Boot("org.test.mvc"), browser));
        KazoWebSiteStart badRoot = await host.StartAsync(new KazoWebSiteStartRequest(new Uri("http://bad.hack/"), Boot("org.test.bad"), browser));

        Assert.Contains("no Blazor root", noRoot.Error);
        Assert.Contains("not a Blazor component", badRoot.Error);
    }

    [Fact]
    public void Finds_the_site_by_scheme_host_and_port()
    {
        AppManifest intranet = SiteManifest("org.test.intranet", "http://intranet.hack/");
        AppManifest wiki = SiteManifest("org.test.wiki", "https://wiki.hack/") with { Web = new WebAppManifest(["https://wiki.hack/", "http://wiki.hack:8080/"]) };
        KazoWebSiteHost host = CreateHost([intranet, wiki]);

        Assert.Equal("org.test.intranet", host.FindSite(new Uri("http://intranet.hack/notes"))?.Id);
        Assert.Equal("org.test.wiki", host.FindSite(new Uri("https://wiki.hack/"))?.Id);
        Assert.Equal("org.test.wiki", host.FindSite(new Uri("http://wiki.hack:8080/page"))?.Id);
        Assert.Null(host.FindSite(new Uri("https://intranet.hack/")));
        Assert.Null(host.FindSite(new Uri("http://wiki.hack/")));
        Assert.Null(host.FindSite(new Uri("http://elsewhere.hack/")));
    }

    private static KazoWebSiteHost CreateHost(
        IReadOnlyList<AppManifest> manifests,
        List<(string, ProcessId, IReadOnlyList<string>)>? starts = null,
        List<(ProcessId, ProcessExitReason)>? stops = null,
        CancellationToken rendererToken = default,
        IReadOnlyList<string>? disabled = null)
    {
        AppCatalogBuildResult built = AppCatalog.Build(manifests, []);
        Assert.True(built.Catalog is not null, string.Join("; ", built.Errors.Select(error => error.ToString())));
        AppCatalog catalog = built.Catalog;
        var enablement = new AppEnablementRegistry(catalog);
        enablement.MarkDisabled(disabled ?? []);
        long nextPid = 100;
        return new KazoWebSiteHost(
            catalog,
            enablement,
            new StubSessionService(CreatePrincipal()),
            (appId, _) => Task.FromResult<(AppDescriptor?, string?)>(
                (new AppDescriptor(catalog.Manifests[appId], typeof(TestRoot), typeof(TestRoot).Assembly), null)),
            (appId, _, parent, arguments, _) =>
            {
                starts?.Add((appId, parent, arguments));
                StubContext context = new(catalog.Manifests[appId], ProcessId.FromInt64(nextPid++), rendererToken);
                return Task.FromResult(new AppLaunchResult(AppLaunchStatus.Launched, Context: context));
            },
            (pid, reason) =>
            {
                stops?.Add((pid, reason));
                return Task.FromResult(true);
            });
    }

    private static KazoBlazorBootManifest Boot(string appId) => new()
    {
        AppId = appId,
        AppName = appId,
        EntryAssembly = AssemblyName,
        RootComponent = typeof(TestRoot).FullName!
    };

    private static AppManifest SiteManifest(string id, string url, Type? root = null) => new()
    {
        Id = id,
        Name = id,
        Version = "1.0.0",
        PublisherId = "pub.hackeros",
        Description = "A site.",
        Kind = AppKind.Web,
        EntryPoint = new AppEntryPointManifest(AssemblyName, typeof(TestRoot).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Hidden, []),
        Resources = AppResourceProfileManifest.None,
        Web = new WebAppManifest([url], new WebBlazorManifest(AssemblyName, (root ?? typeof(TestRoot)).FullName!))
    };

    private static AuthenticatedPrincipal CreatePrincipal()
    {
        LocalGroupId primaryGroup = LocalGroupId.FromGuid(Guid.NewGuid());
        return new AuthenticatedPrincipal(
            SessionId.FromGuid(Guid.NewGuid()),
            LocalUserId.FromGuid(Guid.NewGuid()),
            LocalLoginName.Parse("alice"),
            "Alice",
            AppAuthority.User,
            primaryGroup,
            [primaryGroup],
            InstallationId.FromGuid(Guid.NewGuid()),
            DeviceId.FromGuid(Guid.NewGuid()),
            DateTimeOffset.UtcNow);
    }

    public sealed class TestRoot : KazoComponentBase;

    private sealed class StubSessionService(AuthenticatedPrincipal principal) : ISessionService
    {
        public SessionState State => SessionState.Active;

        public AuthenticatedPrincipal? CurrentPrincipal => principal;

        public Task<AuthenticatedPrincipal> LoginAsync(LocalLoginName loginName, string? password, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public CancellationTokenSource CreateLinkedCancellationSource() => new();
    }

    private sealed class StubContext(AppManifest manifest, ProcessId pid, CancellationToken token = default) : IAppExecutionContext
    {
        public AppManifest Manifest => manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public AppAuthority UserAuthority => AppAuthority.User;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId => pid;
        public CancellationToken CancellationToken => token;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
        public IAppNetworkGateway Network => throw new NotImplementedException();
    }
}
