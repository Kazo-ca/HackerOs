using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.ServerConnection;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.ServerConnection;

/// <summary>State of the periodic synchronization, for the Settings page.</summary>
/// <param name="IsActive">A session is open and the scheduler runs.</param>
/// <param name="IsPaused">The user paused their synchronization (<c>~/.config/hackeros/sync.config</c>).</param>
/// <param name="IsOnline">The last pass reached the server (or none failed yet).</param>
/// <param name="LastPassAtUtc">End of the last completed pass, or <see langword="null"/>.</param>
/// <param name="LastResult">What the last completed pass moved, or <see langword="null"/>.</param>
/// <param name="LastError">Why the last pass failed, or <see langword="null"/>.</param>
/// <param name="Interval">Interval between two periodic passes.</param>
public sealed record SyncSchedulerStatus(
    bool IsActive,
    bool IsPaused,
    bool IsOnline,
    DateTimeOffset? LastPassAtUtc,
    SyncPassResult? LastResult,
    string? LastError,
    TimeSpan Interval);

/// <summary>
/// Synchronizes in the background while a user is logged in and the device is linked to a server
/// (ADR 0101 section 4): every <c>intervalMinutes</c> of <c>/etc/hackeros/sync.config</c> (5 by
/// default), at login and at each reconnection, and <see cref="ChangeDebounce"/> after the last
/// change of a synchronized file, like a sync daemon batching inotify events.
/// </summary>
/// <remarks>
/// <para>
/// A pass is the same sequence the Settings app runs on "Sync now": FileSystem push then pull
/// (push first, so a concurrent edit is caught as a conflict before the pull), Grants pull, then
/// FileAssociations push and pull. Passes never overlap.
/// </para>
/// <para>
/// Without a server, or while disconnected, a pass does nothing and the OS works as before (the
/// Ecosystem host never depends on the server). A failed pass marks the device offline and retries
/// every <see cref="OfflineRetry"/>; the first pass that succeeds again is logged as a reconnection.
/// Each pass is written to <c>/var/log/syslog</c> through the logger (ADR 0045).
/// </para>
/// </remarks>
public sealed class SyncScheduler : IAsyncDisposable
{
    /// <summary>Delay after the last change of a synchronized file before a pass (ADR 0101 section 4).</summary>
    public static readonly TimeSpan ChangeDebounce = TimeSpan.FromSeconds(10);

    /// <summary>Delay between two attempts while the server cannot be reached.</summary>
    public static readonly TimeSpan OfflineRetry = TimeSpan.FromSeconds(30);

    private readonly IFileSystemSyncService _fileSystemSync;
    private readonly IGrantsSyncService? _grantsSync;
    private readonly IFileAssociationsSyncService? _fileAssociationsSync;
    private readonly IServerConnectionService _connection;
    private readonly ISessionService _session;
    private readonly IOsSettingsService? _settings;
    private readonly ITopicMessageBus? _topicBus;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _sync = new();
    private readonly List<IDisposable> _watches = [];
    private readonly IDisposable[] _sessionSubscriptions;

    private CancellationTokenSource? _loop;
    private Task? _loopTask;
    private DateTimeOffset? _changeDueAt;
    private bool _reconnectRequested;
    private bool _passRunning;
    private SyncSchedulerStatus _status = new(false, false, true, null, null, null, TimeSpan.FromMinutes(SyncSettingsDocuments.DefaultIntervalMinutes));

    /// <summary>Creates the scheduler; it does nothing until <see cref="Start"/>.</summary>
    /// <param name="fileSystemSync">Home directory sync.</param>
    /// <param name="grantsSync">Grant pull; optional.</param>
    /// <param name="fileAssociationsSync">File association sync; optional.</param>
    /// <param name="connection">Server link.</param>
    /// <param name="session">Session service.</param>
    /// <param name="settings">Reads <c>sync.config</c>; without it the defaults apply.</param>
    /// <param name="topicBus">Filesystem change topics; without it only the timer triggers passes.</param>
    /// <param name="time">Clock; <see cref="TimeProvider.System"/> by default.</param>
    /// <param name="logger">Logger reaching <c>/var/log/syslog</c>.</param>
    /// <param name="events">Session events: the loop starts at login and stops at logout.</param>
    public SyncScheduler(
        IFileSystemSyncService fileSystemSync,
        IGrantsSyncService? grantsSync,
        IFileAssociationsSyncService? fileAssociationsSync,
        IServerConnectionService connection,
        ISessionService session,
        IOsSettingsService? settings = null,
        ITopicMessageBus? topicBus = null,
        TimeProvider? time = null,
        ILogger<SyncScheduler>? logger = null,
        IEventBus? events = null)
    {
        _fileSystemSync = fileSystemSync ?? throw new ArgumentNullException(nameof(fileSystemSync));
        _grantsSync = grantsSync;
        _fileAssociationsSync = fileAssociationsSync;
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings;
        _topicBus = topicBus;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _sessionSubscriptions = events is null
            ? []
            :
            [
                events.Subscribe<SessionActivatedEvent>(_ => Start()),
                // ADR 0201: the sync follows the foreground session; ending a session in the
                // background leaves the foreground one syncing.
                events.Subscribe<SessionResumedEvent>(_ => Start()),
                events.Subscribe<SessionSwitchedOutEvent>(_ => { Task ignored = StopAsync(); }),
                events.Subscribe<SessionLoggedOutEvent>(_ =>
                {
                    if (_session.CurrentPrincipal is null)
                    {
                        Task ignored = StopAsync();
                    }
                })
            ];
    }

    /// <summary>Raised after each pass and when the scheduler starts or stops.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Gets the current state.</summary>
    public SyncSchedulerStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    /// <summary>
    /// Starts the background loop for the current session (the shell calls it at login); a first
    /// pass runs at once. Calling it again while running does nothing.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_loop is not null)
            {
                return;
            }

            _loop = new CancellationTokenSource();
            _reconnectRequested = true;
            _status = _status with { IsActive = true };
            _loopTask = Task.Run(() => RunAsync(_loop.Token));
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the loop (logout); a pass in progress is cancelled.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? loop;
        Task? task;
        lock (_sync)
        {
            loop = _loop;
            task = _loopTask;
            _loop = null;
            _loopTask = null;
            _status = _status with { IsActive = false };
        }

        if (loop is null)
        {
            return;
        }

        await loop.CancelAsync().ConfigureAwait(false);
        _wake.Release();
        try
        {
            await (task ?? Task.CompletedTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        loop.Dispose();
        ClearWatches();
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Asks for a pass as soon as possible, as after a (re)connection to the server.</summary>
    public void RequestSync()
    {
        lock (_sync)
        {
            _reconnectRequested = true;
        }

        _wake.Release();
    }

    /// <summary>
    /// Records a change of a synchronized file: a pass runs <see cref="ChangeDebounce"/> after the
    /// last one. Changes made while a pass runs (the pull writing files) are ignored.
    /// </summary>
    public void NotifyLocalChange()
    {
        lock (_sync)
        {
            if (_passRunning)
            {
                return;
            }

            _changeDueAt = _time.GetUtcNow() + ChangeDebounce;
        }

        _wake.Release();
    }

    /// <summary>Runs one complete pass now and waits for it (the Settings app's "Sync now").</summary>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>What it moved.</returns>
    /// <exception cref="ServerConnectionException">The server refused a request.</exception>
    /// <exception cref="HttpRequestException">The server could not be reached.</exception>
    public async Task<SyncPassResult> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunPassCoreAsync(manual: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _passGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (IDisposable subscription in _sessionSubscriptions)
        {
            subscription.Dispose();
        }

        await StopAsync().ConfigureAwait(false);
        _passGate.Dispose();
        _wake.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset nextPeriodic = _time.GetUtcNow();
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset now = _time.GetUtcNow();
            bool due;
            DateTimeOffset wakeAt;
            lock (_sync)
            {
                due = _reconnectRequested || now >= nextPeriodic || _changeDueAt is { } changeDue && now >= changeDue;
                wakeAt = _changeDueAt is { } pendingChange && pendingChange < nextPeriodic ? pendingChange : nextPeriodic;
            }

            if (!due)
            {
                TimeSpan delay = wakeAt - now;
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
                try
                {
                    await _wake.WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The delay elapsed: loop and look at what is due.
                }

                continue;
            }

            lock (_sync)
            {
                _reconnectRequested = false;
                _changeDueAt = null;
            }

            bool succeeded = await RunScheduledPassAsync(cancellationToken).ConfigureAwait(false);
            TimeSpan interval = Status.Interval;
            nextPeriodic = _time.GetUtcNow() + (succeeded ? interval : (OfflineRetry < interval ? OfflineRetry : interval));
        }
    }

    private async Task<bool> RunScheduledPassAsync(CancellationToken cancellationToken)
    {
        await _passGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunPassCoreAsync(manual: false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or IOException
                                              or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false; // Already logged and recorded by the pass.
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            // A sync bug must never take the OS down: log it and keep the schedule.
            _logger.LogError(exception, "sync: pass failed unexpectedly");
            return false;
        }
        finally
        {
            _passGate.Release();
        }
    }

    private async Task<SyncPassResult> RunPassCoreAsync(bool manual, CancellationToken cancellationToken)
    {
        (TimeSpan interval, bool paused) = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _status = _status with { Interval = interval, IsPaused = paused };
        }

        ServerConnectionState? state = await _connection.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (_session.CurrentPrincipal is null || state is null || (paused && !manual))
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return SyncPassResult.Empty;
        }

        lock (_sync)
        {
            _passRunning = true;
        }

        try
        {
            SyncPassResult push = await _fileSystemSync.PushAsync(cancellationToken).ConfigureAwait(false);
            SyncPassResult pull = await _fileSystemSync.PullAsync(cancellationToken).ConfigureAwait(false);
            if (_grantsSync is not null)
            {
                await _grantsSync.PullAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_fileAssociationsSync is not null)
            {
                await _fileAssociationsSync.PushAsync(cancellationToken).ConfigureAwait(false);
                await _fileAssociationsSync.PullAsync(cancellationToken).ConfigureAwait(false);
            }

            SyncPassResult result = new(push.Sent + pull.Sent, push.Received + pull.Received, push.Conflicts + pull.Conflicts);
            bool wasOffline;
            lock (_sync)
            {
                wasOffline = !_status.IsOnline;
                _status = _status with { IsOnline = true, LastPassAtUtc = _time.GetUtcNow(), LastResult = result, LastError = null };
            }

            if (wasOffline)
            {
                _logger.LogInformation("sync: server {Server} reachable again", state.ServerBaseUrl);
            }

            _logger.LogInformation(
                "sync: {Trigger} pass: domains filesystem,grants,file_associations; {Sent} sent, {Received} received, {Conflicts} conflict(s)",
                manual ? "manual" : "periodic", result.Sent, result.Received, result.Conflicts);
            StatusChanged?.Invoke(this, EventArgs.Empty);
            await RefreshWatchesAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is ServerConnectionException or HttpRequestException or IOException
                                              or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            bool wasOnline;
            lock (_sync)
            {
                wasOnline = _status.IsOnline;
                _status = _status with { IsOnline = false, LastError = exception.Message };
            }

            if (wasOnline || manual)
            {
                _logger.LogWarning("sync: pass failed, server {Server} unreachable or refused: {Reason}", state.ServerBaseUrl, exception.Message);
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _passRunning = false;
            }
        }
    }

    private async Task<(TimeSpan Interval, bool Paused)> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        if (_settings is null)
        {
            return (TimeSpan.FromMinutes(SyncSettingsDocuments.DefaultIntervalMinutes), false);
        }

        try
        {
            string? system = await _settings.ReadFileAsync(SyncSettingsDocuments.SystemFile, cancellationToken).ConfigureAwait(false);
            LayeredConfigSnapshot snapshot = await _settings.ReadAsync(SyncSettingsDocuments.Document, cancellationToken).ConfigureAwait(false);
            return (SyncSettingsDocuments.ReadInterval(system), SyncSettingsDocuments.IsPaused(snapshot));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (TimeSpan.FromMinutes(SyncSettingsDocuments.DefaultIntervalMinutes), false);
        }
    }

    /// <summary>
    /// Watches every synchronized directory (one subscription per directory, like inotify), so a
    /// change schedules a pass. Refreshed after each pass because directories come and go.
    /// </summary>
    private async Task RefreshWatchesAsync(CancellationToken cancellationToken)
    {
        if (_topicBus is null || _session.CurrentPrincipal is not { } principal)
        {
            return;
        }

        IReadOnlyList<VirtualPath> directories = await _fileSystemSync.ListSynchronizedDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        PublisherIdentity subscriber = new("org.hackeros.sync", principal.LoginName.Value, "sync");
        ClearWatches();
        lock (_sync)
        {
            foreach (VirtualPath directory in directories)
            {
                _watches.Add(_topicBus.Subscribe<FileSystemChangeEvent>(
                    FileSystemTopics.ForDirectory(directory), subscriber, _ => NotifyLocalChange()));
            }
        }
    }

    private void ClearWatches()
    {
        lock (_sync)
        {
            foreach (IDisposable watch in _watches)
            {
                watch.Dispose();
            }

            _watches.Clear();
        }
    }
}
