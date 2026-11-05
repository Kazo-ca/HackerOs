using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.Apps;

/// <summary><see cref="KazoBrowser"/> hands URLs to the Browser app (ADR 0078 §3).</summary>
public sealed class KazoBrowserTests
{
    [Fact]
    public async Task OpenAsync_launches_the_browser_with_the_url()
    {
        FakeAppIntentGateway intents = new();
        using (KazoExecutionContext.Enter(CreateContext(intents)))
        {
            await KazoBrowser.OpenAsync("https://site.hack/");
        }

        (string appId, IReadOnlyList<string> arguments) = Assert.Single(intents.Requests);
        Assert.Equal("org.kazoos.browser", appId);
        Assert.Equal(["https://site.hack/"], arguments);
    }

    [Fact]
    public async Task OpenDetachedAsync_asks_the_browser_for_a_detached_window()
    {
        FakeAppIntentGateway intents = new();
        using (KazoExecutionContext.Enter(CreateContext(intents)))
        {
            await KazoBrowser.OpenDetachedAsync("file:///home/user/site/index.html");
        }

        (string appId, IReadOnlyList<string> arguments) = Assert.Single(intents.Requests);
        Assert.Equal(KazoBrowser.AppId, appId);
        Assert.Equal([KazoBrowser.DetachedArgument, "file:///home/user/site/index.html"], arguments);
    }

    [Fact]
    public async Task An_empty_url_is_refused_before_any_launch()
    {
        FakeAppIntentGateway intents = new();
        using (KazoExecutionContext.Enter(CreateContext(intents)))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => KazoBrowser.OpenDetachedAsync(" "));
        }

        Assert.Empty(intents.Requests);
    }

    [Fact]
    public async Task A_missing_browser_is_reported_as_a_launch_failure()
    {
        FakeAppIntentGateway intents = new FakeAppIntentGateway()
            .WithResult(KazoBrowser.AppId, new AppIntentLaunchResult(AppIntentLaunchOutcome.NotFound));
        using (KazoExecutionContext.Enter(CreateContext(intents)))
        {
            await Assert.ThrowsAsync<KazoAppLaunchException>(() => KazoBrowser.OpenAsync("https://site.hack/"));
        }
    }

    private static MinimalAppExecutionContext CreateContext(FakeAppIntentGateway intents) =>
        new(AppDefinitionMapper.ToManifest(new CallerApp()), intents: intents);

    private sealed class CallerApp : AppDefinition
    {
        public override string Id => "org.example.caller";

        public override string Name => "Caller";

        public override AppKind Kind => AppKind.Window;

        public override Type EntryPoint => typeof(CallerApp);

        public override IReadOnlyList<string> Capabilities => [AppCapabilities.AppsLaunch];
    }
}
