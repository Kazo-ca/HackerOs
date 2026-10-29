using System.Collections.Concurrent;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.FileSystem.HostFolders;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.Tests.FileSystem.HostFolders;

/// <summary>The outside changes of a network drive's watched folders (ADR 0185 §15).</summary>
public sealed class HostFolderChangeTrackerTests
{
    private static readonly VirtualPath MountPath = VirtualPath.Parse("/mnt/work");
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(40);

    private sealed class Activity : IHostActivitySignal
    {
        public bool IsVisible { get; set; } = true;

        public event Action? Resumed;

        public void Resume() => Resumed?.Invoke();
    }

    private sealed record Published(string Path, FileSystemEntryKind EntryKind, long Revision, FileSystemChangeKind Kind);

    private static (HostFolderChangeTracker Tracker, ConcurrentQueue<Published> Changes) Tracker(IHostActivitySignal? activity = null)
    {
        ConcurrentQueue<Published> changes = new();
        HostFolderChangeTracker tracker = new(
            (path, entryKind, revision, kind) => changes.Enqueue(new Published(path.Value, entryKind, revision, kind)),
            activity,
            Interval);
        return (tracker, changes);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int milliseconds = 3000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public void Compare_reports_entries_that_appeared_went_away_or_changed()
    {
        Dictionary<string, (FileSystemEntryKind, long)> before = new()
        {
            ["kept.txt"] = (FileSystemEntryKind.File, 1),
            ["edited.txt"] = (FileSystemEntryKind.File, 1),
            ["gone.txt"] = (FileSystemEntryKind.File, 1),
            ["swap"] = (FileSystemEntryKind.File, 1)
        };
        Dictionary<string, (FileSystemEntryKind, long)> after = new()
        {
            ["kept.txt"] = (FileSystemEntryKind.File, 1),
            ["edited.txt"] = (FileSystemEntryKind.File, 2),
            ["new"] = (FileSystemEntryKind.Directory, 1),
            ["swap"] = (FileSystemEntryKind.Directory, 1)
        };

        IReadOnlyList<HostFolderChange> changes = HostFolderChangeTracker.Compare(before, after);

        Assert.Contains(changes, change => change is { Name: "gone.txt", Kind: FileSystemChangeKind.Deleted });
        Assert.Contains(changes, change => change is { Name: "edited.txt", Kind: FileSystemChangeKind.ContentModified });
        Assert.Contains(changes, change => change is { Name: "new", Kind: FileSystemChangeKind.Created, EntryKind: FileSystemEntryKind.Directory });
        Assert.Contains(changes, change => change is { Name: "swap", Kind: FileSystemChangeKind.Deleted, EntryKind: FileSystemEntryKind.File });
        Assert.Contains(changes, change => change is { Name: "swap", Kind: FileSystemChangeKind.Created, EntryKind: FileSystemEntryKind.Directory });
        Assert.DoesNotContain(changes, change => change.Name == "kept.txt");
        Assert.Equal(5, changes.Count);
    }

    [Fact]
    public async Task A_file_added_on_the_host_is_reported_and_raises_the_folder_revision()
    {
        FakeHostFolder folder = new("work");
        folder.Put("a", "docs", "a.txt");
        HostFolderFileSystemProvider drive = new(MountPath, folder, "alice", "users", connected: true);
        (HostFolderChangeTracker tracker, ConcurrentQueue<Published> changes) = Tracker();
        VirtualPath docs = VirtualPath.Parse("/mnt/work/docs");

        using (tracker.Track(drive, docs))
        {
            await Task.Delay(Interval * 3);
            folder.Put("b", "docs", "b.txt");

            Assert.True(await WaitUntilAsync(() => changes.Any(change => change.Path == "/mnt/work/docs/b.txt")));
        }

        Published created = changes.First(change => change.Path == "/mnt/work/docs/b.txt");
        Assert.Equal(FileSystemChangeKind.Created, created.Kind);
        Assert.Equal(FileSystemEntryKind.File, created.EntryKind);
        Assert.True((await drive.SnapshotAsync(MountPath, CancellationToken.None))["docs"].Revision > 1);
    }

    [Fact]
    public async Task A_hidden_page_is_not_listed_until_it_comes_back()
    {
        FakeHostFolder folder = new("work");
        folder.Put("a", "a.txt");
        HostFolderFileSystemProvider drive = new(MountPath, folder, "alice", "users", connected: true);
        Activity activity = new() { IsVisible = false };
        (HostFolderChangeTracker tracker, ConcurrentQueue<Published> changes) = Tracker(activity);

        using IDisposable tracking = tracker.Track(drive, MountPath);
        await Task.Delay(Interval * 3);
        folder.Put("b", "b.txt");
        await Task.Delay(Interval * 6);
        Assert.Empty(changes);

        activity.Resume();

        Assert.True(await WaitUntilAsync(() => changes.Any(change => change.Path == "/mnt/work/b.txt")));
    }

    [Fact]
    public async Task Nothing_is_reported_once_the_last_watcher_left()
    {
        FakeHostFolder folder = new("work");
        folder.Put("a", "a.txt");
        HostFolderFileSystemProvider drive = new(MountPath, folder, "alice", "users", connected: true);
        (HostFolderChangeTracker tracker, ConcurrentQueue<Published> changes) = Tracker();

        IDisposable first = tracker.Track(drive, MountPath);
        IDisposable second = tracker.Track(drive, MountPath);
        await Task.Delay(Interval * 3);
        first.Dispose();
        folder.Put("b", "b.txt");
        Assert.True(await WaitUntilAsync(() => changes.Any(change => change.Path == "/mnt/work/b.txt")));

        second.Dispose();
        await Task.Delay(Interval * 2);
        int seen = changes.Count;
        folder.Put("c", "c.txt");
        await Task.Delay(Interval * 6);

        Assert.Equal(seen, changes.Count);
    }
}
