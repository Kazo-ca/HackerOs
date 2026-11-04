using System.Threading.Channels;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class KazoFileSystemWatcherTests
{
    [Fact]
    public async Task Watcher_maps_engine_changes_to_FileSystemWatcher_events_inside_the_app_context()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/alice/inbox");
        FakeWatchGateway watch = new();
        MinimalAppExecutionContext context = KazoTestHost.CreateContext(fs, watch);
        Channel<(string Kind, string Path, string? Old, IAppExecutionContext? Ambient)> seen = Channel.CreateUnbounded<(string, string, string?, IAppExecutionContext?)>();

        await using KazoFileSystemWatcher watcher = new("~/inbox", "*.txt");
        watcher.Created += (_, e) => seen.Writer.TryWrite(("created", e.FullPath, null, KazoExecutionContext.CurrentOrNull));
        watcher.Changed += (_, e) => seen.Writer.TryWrite(("changed", e.FullPath, null, KazoExecutionContext.CurrentOrNull));
        watcher.Deleted += (_, e) => seen.Writer.TryWrite(("deleted", e.FullPath, null, KazoExecutionContext.CurrentOrNull));
        watcher.Renamed += (_, e) => seen.Writer.TryWrite(("renamed", e.FullPath, e.OldFullPath, KazoExecutionContext.CurrentOrNull));

        using (KazoExecutionContext.Enter(context))
        {
            await watcher.StartAsync();
        }

        Assert.Equal("/home/alice/inbox", watch.WatchedPath!.Value.Value);
        Assert.Equal(FileSystemWatchScope.ImmediateChildren, watch.Scope);

        watch.Push("/home/alice/inbox/a.txt", FileSystemChangeKind.Created);
        watch.Push("/home/alice/inbox/a.png", FileSystemChangeKind.Created); // filtered out
        watch.Push("/home/alice/inbox/a.txt", FileSystemChangeKind.ContentModified);
        watch.Push("/home/alice/inbox/a.txt", FileSystemChangeKind.MovedFrom, "/home/alice/inbox/b.txt");
        watch.Push("/home/alice/inbox/b.txt", FileSystemChangeKind.MovedFrom, "/home/alice/archive/b.txt");
        watch.Push("/home/alice/inbox/c.txt", FileSystemChangeKind.MovedTo);
        watch.Push("/home/alice/inbox/c.txt", FileSystemChangeKind.Deleted);

        List<(string Kind, string Path, string? Old, IAppExecutionContext? Ambient)> events = [];
        while (events.Count < 6)
        {
            events.Add(await seen.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.Equal(
            [
                ("created", "/home/alice/inbox/a.txt", null),
                ("changed", "/home/alice/inbox/a.txt", null),
                ("renamed", "/home/alice/inbox/b.txt", "/home/alice/inbox/a.txt"),
                ("deleted", "/home/alice/inbox/b.txt", null),
                ("created", "/home/alice/inbox/c.txt", null),
                ("deleted", "/home/alice/inbox/c.txt", null)
            ],
            events.Select(e => (e.Kind, e.Path, e.Old)));
        Assert.All(events, e => Assert.Same(context, e.Ambient));
    }

    [Fact]
    public async Task Dispose_releases_the_subscription()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/alice");
        FakeWatchGateway watch = new();
        KazoFileSystemWatcher watcher = new("/home/alice");
        using (KazoExecutionContext.Enter(KazoTestHost.CreateContext(fs, watch)))
        {
            await watcher.StartAsync();
        }

        Assert.True(watcher.IsRunning);
        await watcher.DisposeAsync();

        Assert.True(watch.Disposed);
        Assert.False(watcher.IsRunning);
    }

    [Fact]
    public async Task Start_validates_the_directory_scope_and_access()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory("/home/alice").WithFile("/home/alice/f");
        FakeWatchGateway watch = new() { Deny = true };
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(fs, watch));

        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => new KazoFileSystemWatcher("missing").StartAsync());
        await Assert.ThrowsAsync<KazoIOException>(() => new KazoFileSystemWatcher("f").StartAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => new KazoFileSystemWatcher("~") { IncludeSubdirectories = true }.StartAsync());
        await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => new KazoFileSystemWatcher("~").StartAsync());
    }
}
