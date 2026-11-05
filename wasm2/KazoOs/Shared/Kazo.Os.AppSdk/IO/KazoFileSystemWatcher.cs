using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.AppSdk.IO;

/// <summary>Describes one change seen by a <see cref="KazoFileSystemWatcher"/> (like <see cref="FileSystemEventArgs"/>, with virtual paths).</summary>
public class KazoFileSystemEventArgs : EventArgs
{
    /// <summary>Initializes the event data.</summary>
    /// <param name="changeType">Kind of change.</param>
    /// <param name="fullPath">Canonical path of the affected entry.</param>
    public KazoFileSystemEventArgs(WatcherChangeTypes changeType, string fullPath)
    {
        ChangeType = changeType;
        FullPath = fullPath;
        Name = KazoPath.GetFileName(fullPath);
    }

    /// <summary>Gets the kind of change.</summary>
    public WatcherChangeTypes ChangeType { get; }

    /// <summary>Gets the canonical path of the affected entry.</summary>
    public string FullPath { get; }

    /// <summary>Gets the name of the affected entry.</summary>
    public string Name { get; }
}

/// <summary>Describes a rename seen by a <see cref="KazoFileSystemWatcher"/> (like <see cref="RenamedEventArgs"/>).</summary>
public sealed class KazoRenamedEventArgs : KazoFileSystemEventArgs
{
    /// <summary>Initializes the event data.</summary>
    public KazoRenamedEventArgs(string fullPath, string oldFullPath)
        : base(WatcherChangeTypes.Renamed, fullPath)
    {
        OldFullPath = oldFullPath;
        OldName = KazoPath.GetFileName(oldFullPath);
    }

    /// <summary>Gets the previous canonical path.</summary>
    public string OldFullPath { get; }

    /// <summary>Gets the previous name.</summary>
    public string OldName { get; }
}

/// <summary>
/// Raises events when entries of one directory are created, changed, deleted or renamed, shaped like
/// <see cref="FileSystemWatcher"/> and built on the app's existing <see cref="IAppFileSystemWatchGateway"/>
/// (ADR 0043). Watching never reveals more than reading the directory already would.
/// </summary>
/// <remarks>
/// <para>
/// Configure <see cref="Path"/>, <see cref="Filter"/> and the events, then call <see cref="StartAsync"/>;
/// dispose with <c>await using</c> to stop. Handlers run on the watcher's delivery loop, which
/// inherits the ambient app context of the <see cref="StartAsync"/> caller, so they can use the
/// <c>Kazo*</c> API directly.
/// </para>
/// <para>
/// The engine currently watches a directory's immediate children only (MSG-013):
/// <see cref="IncludeSubdirectories"/> = <see langword="true"/> makes <see cref="StartAsync"/> throw
/// <see cref="NotSupportedException"/>. An entry moved out of the directory raises
/// <see cref="Deleted"/>; one moved in raises <see cref="Created"/>; a move within it raises <see cref="Renamed"/>.
/// </para>
/// </remarks>
public sealed class KazoFileSystemWatcher : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private ITopicChannelSubscription<FileSystemChangeEvent>? _subscription;
    private Task? _pump;
    private VirtualPath _directory;

    /// <summary>Creates a watcher with no path yet.</summary>
    public KazoFileSystemWatcher()
    {
    }

    /// <summary>Creates a watcher for <paramref name="path"/>.</summary>
    public KazoFileSystemWatcher(string path, string filter = "*")
    {
        Path = path;
        Filter = filter;
    }

    /// <summary>Raised when an entry is created in, or moved into, the directory.</summary>
    public event EventHandler<KazoFileSystemEventArgs>? Created;

    /// <summary>Raised when a file's content or an entry's metadata changes.</summary>
    public event EventHandler<KazoFileSystemEventArgs>? Changed;

    /// <summary>Raised when an entry is deleted from, or moved out of, the directory.</summary>
    public event EventHandler<KazoFileSystemEventArgs>? Deleted;

    /// <summary>Raised when an entry is renamed within the directory.</summary>
    public event EventHandler<KazoRenamedEventArgs>? Renamed;

    /// <summary>Raised when a handler throws or the delivery loop fails; the watcher keeps running after a handler error.</summary>
    public event EventHandler<ErrorEventArgs>? Error;

    /// <summary>Gets or sets the directory to watch (absolute, relative or <c>~</c>-based).</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the name pattern (<c>*</c> and <c>?</c>); default <c>*</c> watches everything.</summary>
    public string Filter { get; set; } = "*";

    /// <summary>Gets or sets whether subdirectories are watched too (not supported by the engine yet).</summary>
    public bool IncludeSubdirectories { get; set; }

    /// <summary>Gets whether <see cref="StartAsync"/> has run and the watcher is not disposed.</summary>
    public bool IsRunning => _pump is { IsCompleted: false };

    /// <summary>Starts delivering events, as the ambient app.</summary>
    /// <exception cref="KazoDirectoryNotFoundException"><see cref="Path"/> is not an existing directory.</exception>
    /// <exception cref="KazoUnauthorizedAccessException">The app cannot read the directory.</exception>
    /// <exception cref="NotSupportedException"><see cref="IncludeSubdirectories"/> is set.</exception>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        if (_subscription is not null)
        {
            throw new InvalidOperationException("The watcher is already started.");
        }

        if (IncludeSubdirectories)
        {
            throw new NotSupportedException("Watching subdirectories is not supported by the filesystem engine yet (MSG-013).");
        }

        KazoPathPattern.Validate(Filter, nameof(Filter));
        _directory = KazoPath.Resolve(Path, nameof(Path));
        IAppExecutionContext app = KazoExecutionContext.Current;
        await KazoFileSystemCore.RequireDirectoryAsync(app.FileSystem, _directory, cancellationToken).ConfigureAwait(false);

        try
        {
            _subscription = await app.Watch.WatchAsync(_directory, FileSystemWatchScope.ImmediateChildren, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException exception)
        {
            throw new KazoUnauthorizedAccessException(
                $"Access to the path '{_directory.Value}' is denied: {exception.Message}",
                FileSystemErrorCode.PermissionDenied,
                _directory.Value,
                exception.Capability);
        }

        // Started from the caller's flow, so the loop (and every handler) inherits its ambient context.
        _pump = PumpAsync(_subscription, _stop.Token);
    }

    /// <summary>Stops delivery and releases the subscription.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
        }

        if (_pump is not null)
        {
            await _pump.ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task PumpAsync(ITopicChannelSubscription<FileSystemChangeEvent> subscription, CancellationToken cancellationToken)
    {
        // Yield first so StartAsync returns before the first delivery.
        await Task.Yield();
        try
        {
            await foreach (TopicMessage<FileSystemChangeEvent> message in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Dispatch(message.Payload);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            Error?.Invoke(this, new ErrorEventArgs(exception));
        }
    }

    private void Dispatch(FileSystemChangeEvent change)
    {
        try
        {
            switch (change.Kind)
            {
                case FileSystemChangeKind.Created:
                case FileSystemChangeKind.MovedTo:
                    Raise(Created, new KazoFileSystemEventArgs(WatcherChangeTypes.Created, change.Path.Value));
                    break;

                case FileSystemChangeKind.ContentModified:
                case FileSystemChangeKind.MetadataModified:
                    Raise(Changed, new KazoFileSystemEventArgs(WatcherChangeTypes.Changed, change.Path.Value));
                    break;

                case FileSystemChangeKind.Deleted:
                    Raise(Deleted, new KazoFileSystemEventArgs(WatcherChangeTypes.Deleted, change.Path.Value));
                    break;

                case FileSystemChangeKind.MovedFrom when change.MovedToPath is { } destination
                                                         && KazoFileSystemCore.ParentOf(destination) == _directory:
                    KazoRenamedEventArgs renamed = new(destination.Value, change.Path.Value);
                    if (KazoPathPattern.IsMatch(renamed.Name, Filter) || KazoPathPattern.IsMatch(renamed.OldName, Filter))
                    {
                        Renamed?.Invoke(this, renamed);
                    }

                    break;

                case FileSystemChangeKind.MovedFrom:
                    Raise(Deleted, new KazoFileSystemEventArgs(WatcherChangeTypes.Deleted, change.Path.Value));
                    break;
            }
        }
        catch (Exception exception)
        {
            Error?.Invoke(this, new ErrorEventArgs(exception));
        }
    }

    private void Raise(EventHandler<KazoFileSystemEventArgs>? handler, KazoFileSystemEventArgs args)
    {
        if (KazoPathPattern.IsMatch(args.Name, Filter))
        {
            handler?.Invoke(this, args);
        }
    }
}
