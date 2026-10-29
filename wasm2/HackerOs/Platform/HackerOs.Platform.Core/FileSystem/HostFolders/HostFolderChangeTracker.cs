using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.Core.FileSystem.HostFolders;

/// <summary>
/// Finds the changes made on the host computer in the watched folders of network drives (ADR 0185 §15)
/// and hands them to the same change notifications as the OS's own writes, so every app that watches
/// the folder sees them. While a folder is watched, it is listed again every
/// <see cref="DefaultInterval"/> when the page is visible, at once when the page comes back, and at once
/// when the browser's <c>FileSystemObserver</c> reports a change (<see cref="IObservableHostFolder"/>).
/// Each listing is compared with the previous one: an entry that appeared, went away or changed is
/// reported, and the folder's revision is raised.
/// </summary>
internal sealed class HostFolderChangeTracker
{
    /// <summary>How often a watched folder is listed again while the page is visible.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, FolderWatch> _folders = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Action<VirtualPath, FileSystemEntryKind, long, FileSystemChangeKind> _publish;
    private readonly IHostActivitySignal? _activity;
    private readonly TimeSpan _interval;

    /// <summary>Initializes the tracker.</summary>
    /// <param name="publish">Publishes one change: the entry, its kind, its revision, what happened.</param>
    /// <param name="activity">Whether the page is visible; <see langword="null"/> counts it always visible.</param>
    /// <param name="interval">How often a watched folder is listed; <see cref="DefaultInterval"/> by default.</param>
    public HostFolderChangeTracker(
        Action<VirtualPath, FileSystemEntryKind, long, FileSystemChangeKind> publish,
        IHostActivitySignal? activity,
        TimeSpan? interval = null)
    {
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _activity = activity;
        _interval = interval ?? DefaultInterval;
    }

    /// <summary>Starts following <paramref name="directory"/> of <paramref name="drive"/>, until the result is disposed.</summary>
    /// <remarks>Several watchers of one folder share one listing loop.</remarks>
    public IDisposable Track(HostFolderFileSystemProvider drive, VirtualPath directory)
    {
        ArgumentNullException.ThrowIfNull(drive);
        FolderWatch watch;
        lock (_gate)
        {
            if (!_folders.TryGetValue(directory.Value, out watch!))
            {
                watch = new FolderWatch(this, drive, directory);
                _folders[directory.Value] = watch;
                watch.Start();
            }

            watch.References++;
        }

        return new Release(this, watch);
    }

    private void Untrack(FolderWatch watch)
    {
        lock (_gate)
        {
            if (--watch.References > 0)
            {
                return;
            }

            _folders.Remove(watch.Directory.Value);
        }

        watch.Stop();
    }

    private sealed class Release(HostFolderChangeTracker owner, FolderWatch watch) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Untrack(watch);
            }
        }
    }

    /// <summary>One watched folder: its last listing and its loop.</summary>
    private sealed class FolderWatch(HostFolderChangeTracker owner, HostFolderFileSystemProvider drive, VirtualPath directory)
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _wake = new(0, 1);
        private IReadOnlyDictionary<string, (FileSystemEntryKind Kind, long Revision)>? _last;

        public VirtualPath Directory { get; } = directory;

        public int References { get; set; }

        public void Start()
        {
            if (owner._activity is { } activity)
            {
                activity.Resumed += Wake;
            }

            _ = RunAsync(_stop.Token);
        }

        public void Stop()
        {
            if (owner._activity is { } activity)
            {
                activity.Resumed -= Wake;
            }

            _stop.Cancel();
        }

        /// <summary>Lists the folder now rather than at the next tick.</summary>
        public void Wake()
        {
            try
            {
                if (_wake.CurrentCount == 0)
                {
                    _wake.Release();
                }
            }
            catch (Exception exception) when (exception is SemaphoreFullException or ObjectDisposedException)
            {
                // Already woken, or stopped.
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            IAsyncDisposable? observer = null;
            try
            {
                if (drive.Folder is IObservableHostFolder observable)
                {
                    try
                    {
                        observer = await observable.ObserveAsync(drive.RelativeSegments(Directory), Wake, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
                    {
                        // No observer: the timer alone.
                    }
                }

                _last = await TryListAsync(cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested)
                {
                    bool woken = await _wake.WaitAsync(owner._interval, cancellationToken).ConfigureAwait(false);
                    if (woken || owner._activity is null || owner._activity.IsVisible)
                    {
                        await ScanAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // No longer watched.
            }
            finally
            {
                if (observer is not null)
                {
                    try
                    {
                        await observer.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
                    {
                        // The page is gone: the observer went with it.
                    }
                }

                _stop.Dispose();
                _wake.Dispose();
            }
        }

        private async Task<IReadOnlyDictionary<string, (FileSystemEntryKind Kind, long Revision)>?> TryListAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await drive.SnapshotAsync(Directory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                // Disconnected, or the folder is gone: nothing to compare until it lists again.
                return null;
            }
        }

        private async Task ScanAsync(CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, (FileSystemEntryKind Kind, long Revision)>? current = await TryListAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            IReadOnlyDictionary<string, (FileSystemEntryKind Kind, long Revision)>? previous = _last;
            _last = current;
            if (previous is null)
            {
                return;
            }

            IReadOnlyList<HostFolderChange> changes = Compare(previous, current);
            if (changes.Count == 0)
            {
                return;
            }

            long folderRevision = drive.NoteHostChange(Directory);
            string parent = Directory.Value.TrimEnd('/');
            foreach (HostFolderChange change in changes)
            {
                VirtualPath path = VirtualPath.Parse($"{parent}/{change.Name}");
                owner._publish(path, change.EntryKind, change.Kind == FileSystemChangeKind.Deleted ? folderRevision : change.Revision, change.Kind);
            }
        }
    }

    /// <summary>
    /// The changes between two listings of one folder: entries that appeared (<c>Created</c>), went away
    /// (<c>Deleted</c>) or changed revision (<c>ContentModified</c>); an entry that changed kind is
    /// deleted then created.
    /// </summary>
    internal static IReadOnlyList<HostFolderChange> Compare(
        IReadOnlyDictionary<string, (FileSystemEntryKind Kind, long Revision)> previous,
        IReadOnlyDictionary<string, (FileSystemEntryKind Kind, long Revision)> current)
    {
        List<HostFolderChange> changes = [];
        foreach ((string name, (FileSystemEntryKind kind, long revision)) in previous)
        {
            if (!current.TryGetValue(name, out (FileSystemEntryKind Kind, long Revision) now) || now.Kind != kind)
            {
                changes.Add(new HostFolderChange(name, FileSystemChangeKind.Deleted, kind, revision));
            }
        }

        foreach ((string name, (FileSystemEntryKind kind, long revision)) in current)
        {
            if (!previous.TryGetValue(name, out (FileSystemEntryKind Kind, long Revision) before) || before.Kind != kind)
            {
                changes.Add(new HostFolderChange(name, FileSystemChangeKind.Created, kind, revision));
            }
            else if (before.Revision != revision)
            {
                changes.Add(new HostFolderChange(name, FileSystemChangeKind.ContentModified, kind, revision));
            }
        }

        return changes;
    }
}

/// <summary>One change found in a host folder: the entry's name, what happened, its kind and revision.</summary>
internal sealed record HostFolderChange(string Name, FileSystemChangeKind Kind, FileSystemEntryKind EntryKind, long Revision);
