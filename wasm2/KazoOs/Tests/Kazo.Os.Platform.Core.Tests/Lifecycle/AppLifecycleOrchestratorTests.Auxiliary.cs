using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Lifecycle;

/// <summary>
/// ADR 0084 §3: the renderer of a Blazor site is an auxiliary process of the web app, child of
/// the browser's process, without the listening capabilities, and never the site's instance.
/// </summary>
public sealed partial class AppLifecycleOrchestratorTests
{
    [Fact]
    public async Task A_renderer_is_a_child_of_the_browser_without_listening_and_leaves_the_site_startable()
    {
        Fixture fixture = new(WaiterManifest(), BlazorSiteManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        foreach (string capability in new[] { AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead })
        {
            fixture.Grants.Grant("org.test.blazor", principal.UserId.ToString(), capability, CapabilityGrantSource.UserApproval, AppAuthority.Administrator);
        }

        AppLaunchResult browser = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.waiter", principal, []));
        ProcessId browserPid = browser.Process!.Pid;

        AppLaunchResult renderer = await fixture.Orchestrator.StartAuxiliaryAsync(
            "org.test.blazor", principal, browserPid, ["--type=renderer", "http://blazor.hack/notes"]);

        Assert.Equal(AppLaunchStatus.Launched, renderer.Status);
        ProcessRecord process = renderer.Process!;
        Assert.Equal(browserPid, process.ParentPid);
        Assert.Equal(["org.test.blazor", "--type=renderer", "http://blazor.hack/notes"], fixture.Manager.Environments.Get(process.Pid).Arguments);
        IAppExecutionContext context = renderer.Context!;
        Assert.Equal("org.test.blazor", context.Manifest.Id);
        Assert.Contains(AppCapabilities.NetworkSimulatedRead, context.GrantedCapabilities);
        Assert.DoesNotContain(AppCapabilities.NetworkListen, context.GrantedCapabilities);
        // The renderer is not the site: the site's own instance can still start, once.
        Assert.False(fixture.Manager.TryGetSingleton("org.test.blazor", out _));
        AppLaunchResult site = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.test.blazor", principal, [], Interactive: false));
        Assert.True(site.Status == AppLaunchStatus.Launched, $"{site.Status}: {site.ErrorCode} {site.ErrorMessage} {site.StandardError}");
        Assert.NotEqual(process.Pid, site.Process!.Pid);

        // A crash of the site's components ends the renderer in fault (the site's process is its own).
        Assert.True(fixture.Manager.TryGetActive(process.Pid, out _));
        Assert.True(await fixture.Orchestrator.StopAsync(process.Pid, ProcessExitReason.Fault));
        Assert.False(fixture.Manager.TryGetActive(process.Pid, out _));
        Assert.True(context.CancellationToken.IsCancellationRequested);

        await fixture.Orchestrator.StopAllAsync(ProcessExitReason.Shutdown);
    }

    [Fact]
    public async Task Only_a_web_app_has_a_renderer()
    {
        Fixture fixture = new(WaiterManifest());
        AuthenticatedPrincipal principal = await fixture.LoginAsync();
        AppLaunchResult browser = await fixture.Orchestrator.LaunchAsync(new AppLaunchRequest("org.kazoos.waiter", principal, []));

        AppLaunchResult notWeb = await fixture.Orchestrator.StartAuxiliaryAsync("org.kazoos.waiter", principal, browser.Process!.Pid, []);
        AppLaunchResult unknown = await fixture.Orchestrator.StartAuxiliaryAsync("org.test.missing", principal, browser.Process!.Pid, []);

        Assert.Equal(AppLaunchStatus.NotFound, notWeb.Status);
        Assert.Equal("lifecycle.auxiliary.not-web", notWeb.ErrorCode);
        Assert.Equal(AppLaunchStatus.NotFound, unknown.Status);

        await fixture.Orchestrator.StopAllAsync(ProcessExitReason.Shutdown);
    }

    private static AppManifest BlazorSiteManifest() => new()
    {
        Id = "org.test.blazor",
        Name = "Blazor site",
        Version = "1.0.0",
        PublisherId = "org.test",
        Description = "A web app with a Blazor root.",
        Kind = AppKind.Web,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.Platform.Core.Tests", typeof(BlazorSiteApp).FullName!),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Hidden, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.NetworkListen, AppCapabilities.NetworkSimulatedRead],
        Web = new WebAppManifest(["http://blazor.hack/"], new WebBlazorManifest("Kazo.Os.Platform.Core.Tests", typeof(BlazorSiteApp).FullName!))
    };

    /// <summary>The site's server side: a web app that answers nothing useful (the renderer is what these tests are about).</summary>
    private sealed class BlazorSiteApp(AppManifest manifest) : WebServerAppBase(manifest)
    {
        protected override Task ProcessRequestAsync(KazoHttpListenerContext context, CancellationToken cancellationToken)
        {
            context.Response.Write("site");
            return Task.CompletedTask;
        }
    }
}
