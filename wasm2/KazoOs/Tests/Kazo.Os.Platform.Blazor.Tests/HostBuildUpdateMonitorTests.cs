using System.Reflection;
using Kazo.Os.Platform.Blazor.LazyLoading;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>ADR 0162: an app that no longer loads because the origin serves another build.</summary>
public sealed class HostBuildUpdateMonitorTests
{
    private const string Running = "_framework/dotnet.5kv7vq3e4n.js";
    private const string Rebuilt = "_framework/dotnet.bdig46o9on.js";

    [Fact]
    public void The_identity_is_the_fingerprinted_dotnet_js_and_not_the_import_map_keys()
    {
        const string markup = """
            <script type="importmap">{ "imports": {
              "./_framework/dotnet.js": "./_framework/dotnet.5kv7vq3e4n.js",
              "./_framework/dotnet.native.js": "./_framework/dotnet.native.q38003gvqi.js",
              "./_framework/dotnet.runtime.js": "./_framework/dotnet.runtime.azrfhwb804.js" } }</script>
            """;

        Assert.Equal(Running, HostBuildIdentity.FromMarkup(markup));
        Assert.Null(HostBuildIdentity.FromMarkup("<script src=\"_framework/dotnet.native.js\"></script>"));
        Assert.Null(HostBuildIdentity.FromMarkup(null));
    }

    [Fact]
    public async Task Another_served_build_is_an_update_raised_once()
    {
        var monitor = new HostBuildUpdateMonitor(new FixedProbe(Running, Rebuilt));
        int raised = 0;
        monitor.UpdatePending += () => raised++;

        Assert.True(await monitor.CheckAsync());
        Assert.True(await monitor.CheckAsync());

        Assert.True(monitor.IsUpdatePending);
        Assert.Equal(1, raised);
    }

    [Theory]
    [InlineData(Running, Running)]
    [InlineData(Running, null)]
    [InlineData(null, Rebuilt)]
    public async Task The_same_build_or_an_unknown_one_is_not_an_update(string? running, string? served)
    {
        var monitor = new HostBuildUpdateMonitor(new FixedProbe(running, served));

        Assert.False(await monitor.CheckAsync());
        Assert.False(monitor.IsUpdatePending);
    }

    [Fact]
    public async Task A_probe_that_throws_is_not_an_update()
    {
        var monitor = new HostBuildUpdateMonitor(new ThrowingProbe());

        Assert.False(await monitor.CheckAsync());
    }

    [Fact]
    public async Task A_failed_load_after_a_rebuild_reports_the_changed_build()
    {
        var monitor = new HostBuildUpdateMonitor(new FixedProbe(Running, Rebuilt));
        var registry = new BuildKnownAssemblyLoaderRegistry(["Kazo.Os.Apps.Settings.dll"], new FailingTransport(), monitor);

        BuildKnownAssemblyLoadOutcome outcome = await registry.LoadAsync("Kazo.Os.Apps.Settings.dll");

        Assert.Equal(BuildKnownAssemblyLoadStatus.HostBuildChanged, outcome.Status);
        Assert.True(monitor.IsUpdatePending);
    }

    [Fact]
    public async Task A_failed_load_on_the_same_build_keeps_the_transport_error()
    {
        var monitor = new HostBuildUpdateMonitor(new FixedProbe(Running, Running));
        var registry = new BuildKnownAssemblyLoaderRegistry(["Kazo.Os.Apps.Settings.dll"], new FailingTransport(), monitor);

        BuildKnownAssemblyLoadOutcome outcome = await registry.LoadAsync("Kazo.Os.Apps.Settings.dll");

        Assert.Equal(BuildKnownAssemblyLoadStatus.Failed, outcome.Status);
        Assert.Equal(FailingTransport.Message, outcome.Detail);
    }

    private sealed class FixedProbe(string? running, string? served) : IHostBuildProbe
    {
        public ValueTask<string?> GetRunningBuildAsync(CancellationToken cancellationToken) => ValueTask.FromResult(running);
        public ValueTask<string?> GetServedBuildAsync(CancellationToken cancellationToken) => ValueTask.FromResult(served);
    }

    private sealed class ThrowingProbe : IHostBuildProbe
    {
        public ValueTask<string?> GetRunningBuildAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("JS unavailable");
        public ValueTask<string?> GetServedBuildAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("JS unavailable");
    }

    private sealed class FailingTransport : IBuildKnownAssemblyTransport
    {
        public const string Message = "download failed 0 TypeError: Failed to fetch";

        public Task<IReadOnlyList<Assembly>> LoadAsync(IReadOnlyList<string> assemblyNames, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(Message);
    }
}
