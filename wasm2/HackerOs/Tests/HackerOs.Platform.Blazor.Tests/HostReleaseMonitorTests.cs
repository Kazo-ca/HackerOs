using HackerOs.Platform.Blazor.LazyLoading;

namespace HackerOs.Platform.Blazor.Tests;

/// <summary>ADR 0174: the OS reads the version.json its origin serves and reports a newer release.</summary>
public sealed class HostReleaseMonitorTests
{
    private const string Running = "2.1.0";

    [Theory]
    [InlineData("""{ "product": "HackerOS", "version": "2.2.0" }""", "2.2.0")]
    [InlineData("""{ "version": " 3.0.0-preview.1 " }""", "3.0.0-preview.1")]
    [InlineData("""{ "product": "HackerOS" }""", null)]
    [InlineData("""{ "version": 2 }""", null)]
    [InlineData("""[ "2.2.0" ]""", null)]
    [InlineData("<!DOCTYPE html>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_version_is_the_version_string_of_the_document(string? document, string? expected) =>
        Assert.Equal(expected, HostReleaseMonitor.ParseVersion(document));

    [Fact]
    public async Task Another_served_version_is_reported_once_per_version()
    {
        var probe = new SequenceProbe("""{ "version": "2.2.0" }""");
        var monitor = new HostReleaseMonitor(probe, Running);
        List<string> reported = [];
        monitor.ReleaseAvailable += reported.Add;

        Assert.True(await monitor.CheckAsync());
        Assert.True(await monitor.CheckAsync());
        probe.Document = """{ "version": "2.3.0" }""";
        Assert.True(await monitor.CheckAsync());

        Assert.Equal(["2.2.0", "2.3.0"], reported);
        Assert.Equal("2.3.0", monitor.AvailableVersion);
    }

    [Theory]
    [InlineData("""{ "version": "2.1.0" }""")]
    [InlineData(null)]
    [InlineData("not json")]
    public async Task The_running_version_or_an_unreadable_file_is_not_an_update(string? document)
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe(document), Running);
        monitor.ReleaseAvailable += _ => Assert.Fail("No release expected.");

        Assert.False(await monitor.CheckAsync());
        Assert.Null(monitor.AvailableVersion);
    }

    [Fact]
    public async Task A_probe_that_throws_is_not_an_update()
    {
        var monitor = new HostReleaseMonitor(new ThrowingProbe(), Running);

        Assert.False(await monitor.CheckAsync());
    }

    [Fact]
    public async Task Each_check_also_asks_whether_the_build_changed_under_the_page()
    {
        var buildMonitor = new HostBuildUpdateMonitor(new FixedBuildProbe("_framework/dotnet.5kv7vq3e4n.js", "_framework/dotnet.bdig46o9on.js"));
        var monitor = new HostReleaseMonitor(new SequenceProbe("""{ "version": "2.1.0" }"""), Running, buildMonitor);

        Assert.False(await monitor.CheckAsync());

        Assert.True(buildMonitor.IsUpdatePending);
    }

    [Fact]
    public async Task Started_it_checks_at_each_interval_until_disposed()
    {
        var probe = new SequenceProbe("""{ "version": "2.2.0" }""");
        await using var monitor = new HostReleaseMonitor(probe, Running);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.ReleaseAvailable += version => reported.TrySetResult(version);

        monitor.Start(TimeSpan.FromMilliseconds(20));
        monitor.Start(TimeSpan.FromMilliseconds(20));

        Assert.Equal("2.2.0", await reported.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void The_interval_must_be_positive_and_the_running_version_known()
    {
        var monitor = new HostReleaseMonitor(new SequenceProbe(null), Running);

        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Start(TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new HostReleaseMonitor(new SequenceProbe(null), " "));
        Assert.Equal(TimeSpan.FromMinutes(5), HostReleaseMonitor.DefaultInterval);
    }

    private sealed class SequenceProbe(string? document) : IHostReleaseProbe
    {
        public string? Document { get; set; } = document;

        public ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Document);
    }

    private sealed class ThrowingProbe : IHostReleaseProbe
    {
        public ValueTask<string?> GetServedVersionDocumentAsync(CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }

    private sealed class FixedBuildProbe(string? running, string? served) : IHostBuildProbe
    {
        public ValueTask<string?> GetRunningBuildAsync(CancellationToken cancellationToken) => ValueTask.FromResult(running);
        public ValueTask<string?> GetServedBuildAsync(CancellationToken cancellationToken) => ValueTask.FromResult(served);
    }
}
