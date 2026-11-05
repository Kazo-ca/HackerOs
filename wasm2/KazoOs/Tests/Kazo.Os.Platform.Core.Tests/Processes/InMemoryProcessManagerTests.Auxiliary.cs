using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>ADR 0084 §3: an auxiliary process (a site's renderer) is never the app's singleton instance.</summary>
public sealed partial class InMemoryProcessManagerTests
{
    [Fact]
    public void An_auxiliary_process_is_not_the_apps_singleton()
    {
        Fixture fixture = new();
        ProcessRecord browser = fixture.Manager.Start(fixture.RequestFor("org.kazoos.browser"));
        ProcessRecord renderer = fixture.Manager.Start(fixture.RequestFor("org.test.site", browser.Pid) with { Auxiliary = true });

        Assert.False(fixture.Manager.TryGetSingleton("org.test.site", out _));

        ProcessRecord site = fixture.Manager.Start(fixture.RequestFor("org.test.site"));

        Assert.True(fixture.Manager.TryGetSingleton("org.test.site", out ProcessRecord found));
        Assert.Equal(site.Pid, found.Pid);
        Assert.Equal(browser.Pid, renderer.ParentPid);
    }
}
