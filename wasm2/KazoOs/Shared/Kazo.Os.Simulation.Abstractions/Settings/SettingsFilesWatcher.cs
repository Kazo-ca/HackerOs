using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>
/// Watches the files of one layered settings document and reports any change to them, whoever
/// made it (<c>nano</c>, the Settings app, another instance), so the running reader can reload
/// (ADR 0044 section 7).
/// </summary>
/// <remarks>
/// <para>
/// The filesystem publishes changes per directory (<see cref="FileSystemTopics.ForDirectory"/>), so
/// this watcher subscribes to the parent directory of each file. When that directory does not
/// exist yet (a user who never saved a setting has no <c>~/.config/&lt;appId&gt;/</c>), it watches
/// the nearest existing ancestor instead and moves down as soon as the directory is created.
/// </para>
/// <para>
/// Watching is best effort: a directory the caller may not read, or a context whose watch gateway
/// is unsupported, simply produces no notifications. Readers still see the current content on
/// their next explicit read.
/// </para>
/// </remarks>
public sealed class SettingsFilesWatcher : IAsyncDisposable
{
    private readonly IAppFileSystemWatchGateway _watch;
    private readonly HashSet<string> _files;
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly Func<ValueTask> _onChanged;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<ArmedWatch> _armed = [];
    private readonly object _sync = new();

    /// <summary>Creates a watcher; call <see cref="StartAsync"/> to begin.</summary>
    /// <param name="watch">Authorized watch gateway of the reader.</param>
    /// <param name="files">Files to watch.</param>
    /// <param name="onChanged">Invoked after any change to one of <paramref name="files"/>.</param>
    public SettingsFilesWatcher(IAppFileSystemWatchGateway watch, IEnumerable<VirtualPath> files, Func<ValueTask> onChanged)
    {
        _watch = watch ?? throw new ArgumentNullException(nameof(watch));
        ArgumentNullException.ThrowIfNull(files);
        _files = new HashSet<string>(files.Select(static file => file.Value), StringComparer.Ordinal);
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
    }

    /// <summary>
    /// Creates a watcher reporting any change to the files directly inside <paramref name="directories"/>
    /// (a folder of drop-in files, such as the theme folders of ADR 0103), with the same fallback to
    /// the nearest existing ancestor while a folder does not exist yet.
    /// </summary>
    /// <param name="watch">Authorized watch gateway of the reader.</param>
    /// <param name="directories">Folders whose immediate children are watched.</param>
    /// <param name="onChanged">Invoked after any change inside one of <paramref name="directories"/>.</param>
    public static SettingsFilesWatcher ForDirectories(
        IAppFileSystemWatchGateway watch, IEnumerable<VirtualPath> directories, Func<ValueTask> onChanged)
    {
        ArgumentNullException.ThrowIfNull(directories);
        SettingsFilesWatcher watcher = new(watch, [], onChanged);
        foreach (VirtualPath directory in directories)
        {
            watcher._directories.Add(directory.Value);
        }

        return watcher;
    }

    /// <summary>Subscribes to the directories of every watched file (and to every watched directory).</summary>
    /// <param name="cancellationToken">Cancels the subscription attempts.</param>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (string directory in _files
            .Select(static file => SettingsTextFile.GetParent(VirtualPath.Parse(file)).Value)
            .Concat(_directories)
            .Distinct(StringComparer.Ordinal))
        {
            await ArmAsync(VirtualPath.Parse(directory), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        ArmedWatch[] armed;
        lock (_sync)
        {
            armed = [.. _armed];
            _armed.Clear();
        }

        foreach (ArmedWatch watch in armed)
        {
            await watch.Subscription.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
    }

    private async ValueTask ArmAsync(VirtualPath target, CancellationToken cancellationToken)
    {
        VirtualPath candidate = target;
        while (!_stopping.IsCancellationRequested)
        {
            ITopicChannelSubscription<FileSystemChangeEvent>? subscription = await TryWatchAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (subscription is not null)
            {
                ArmedWatch armed = new(target, candidate, subscription);
                lock (_sync)
                {
                    _armed.Add(armed);
                }

                _ = PumpAsync(armed);
                return;
            }

            VirtualPath parent = SettingsTextFile.GetParent(candidate);
            if (parent.Value == candidate.Value)
            {
                return;
            }

            candidate = parent;
        }
    }

    private async ValueTask<ITopicChannelSubscription<FileSystemChangeEvent>?> TryWatchAsync(
        VirtualPath directory, CancellationToken cancellationToken)
    {
        try
        {
            return await _watch.WatchAsync(directory, FileSystemWatchScope.ImmediateChildren, cancellationToken).ConfigureAwait(false);
        }
        catch (AppGatewayAccessDeniedException)
        {
            // Missing or unreadable directory: the caller tries the parent next.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private async Task PumpAsync(ArmedWatch armed)
    {
        try
        {
            await foreach (TopicMessage<FileSystemChangeEvent> message in armed.Subscription.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                FileSystemChangeEvent change = message.Payload;
                if (armed.Watched.Value != armed.Target.Value)
                {
                    // Watching an ancestor while the real directory is missing: move down once it
                    // (or one of its ancestors) appears, then reload in case it already holds a file.
                    if (IsAncestorOrSelf(change.Path, armed.Target) || (change.MovedToPath is { } moved && IsAncestorOrSelf(moved, armed.Target)))
                    {
                        lock (_sync)
                        {
                            _armed.Remove(armed);
                        }

                        await armed.Subscription.DisposeAsync().ConfigureAwait(false);
                        await ArmAsync(armed.Target, _stopping.Token).ConfigureAwait(false);
                        await _onChanged().ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                if (_files.Contains(change.Path.Value)
                    || (change.MovedToPath is { } movedTo && _files.Contains(movedTo.Value))
                    || _directories.Contains(armed.Target.Value))
                {
                    await _onChanged().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a message was being handled.
        }
    }

    private static bool IsAncestorOrSelf(VirtualPath candidate, VirtualPath target) =>
        target.Value == candidate.Value || target.Value.StartsWith($"{candidate.Value}/", StringComparison.Ordinal);

    private sealed record ArmedWatch(
        VirtualPath Target,
        VirtualPath Watched,
        ITopicChannelSubscription<FileSystemChangeEvent> Subscription);
}
