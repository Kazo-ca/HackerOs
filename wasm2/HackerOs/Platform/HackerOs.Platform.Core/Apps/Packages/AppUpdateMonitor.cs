using System.Globalization;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HackerOs.Platform.Core.Apps.Packages;

/// <summary>
/// The update check of the Hack Store (ADR 0188 §3, ADR 0212 §5): at sign-in, then every
/// <c>[updates] interval-hours</c> of <c>/etc/hackeros/app-sources.config</c>, the OS looks for a
/// newer version of every app installed for the system or for the session user. A notification
/// opens the Store on its updates; no badge is drawn on any icon (ADR 0215 §1, Benoit: notification only).
/// </summary>
/// <remarks>
/// <para>
/// Nothing is installed by itself: the update is one click in the Store, with the same rights as
/// the install (ADR 0189). The OS downloads only the catalogs and the <c>index.json</c> files, from
/// the hosts of <c>app-sources.config</c>, never through an app.
/// </para>
/// <para>
/// A notification is posted only when the set of available updates changes, so a check every six
/// hours that finds the same update says it once. The Store's own check
/// (<c>HackAppPackages.GetUpdatesAsync</c>) goes through <see cref="Publish"/> too, so an update the
/// Store already shows is not notified again.
/// </para>
/// </remarks>
public sealed class AppUpdateMonitor : IDisposable
{
    /// <summary>The Hack Store (ADR 0188 §1), which the notification opens.</summary>
    public const string StoreAppId = "org.hackeros.store";

    /// <summary>Argument that opens the Store on its installed apps and their updates.</summary>
    public const string UpdatesPageArgument = "--page=installed";

    private readonly AppPackageInstaller _installer;
    private readonly Func<IAppFileSystemGateway> _systemReader;
    private readonly Func<AuthenticatedPrincipal?> _currentUser;
    private readonly INotificationQueue? _notifications;
    private readonly Func<CultureInfo> _language;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private IReadOnlyList<AppPackageUpdate> _pending = [];
    private LocalUserId? _pendingUser;
    private string _notifiedKey = string.Empty;
    private CancellationTokenSource? _loop;

    /// <summary>Creates the monitor.</summary>
    /// <param name="installer">Reads the sources and downloads the catalogs.</param>
    /// <param name="systemReader">The OS's own reader of <c>/etc</c> and of the session user's home.</param>
    /// <param name="currentUser">The session user (resolved at each check).</param>
    /// <param name="notifications">Receives the notification; <see langword="null"/> posts none.</param>
    /// <param name="language">Language of the notification; English when <see langword="null"/>.</param>
    /// <param name="time">Clock of the interval; the system clock by default.</param>
    /// <param name="logger">Writes the failures to <c>/var/log/syslog</c>.</param>
    public AppUpdateMonitor(
        AppPackageInstaller installer,
        Func<IAppFileSystemGateway> systemReader,
        Func<AuthenticatedPrincipal?> currentUser,
        INotificationQueue? notifications = null,
        Func<CultureInfo>? language = null,
        TimeProvider? time = null,
        ILogger<AppUpdateMonitor>? logger = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _systemReader = systemReader ?? throw new ArgumentNullException(nameof(systemReader));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _notifications = notifications;
        _language = language ?? (static () => CultureInfo.GetCultureInfo("en"));
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Raised when the number of available updates of the session user changes.</summary>
    public event Action? Changed;

    /// <summary>Gets the updates found by the last check, for the session user.</summary>
    public IReadOnlyList<AppPackageUpdate> Pending
    {
        get
        {
            lock (_gate)
            {
                return _pendingUser is { } user && _currentUser()?.UserId == user ? _pending : [];
            }
        }
    }

    /// <summary>Records what a check found for the session user, and raises <see cref="Changed"/> when the count moves.</summary>
    /// <param name="updates">The available updates.</param>
    public void Publish(IReadOnlyList<AppPackageUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        LocalUserId? user = _currentUser()?.UserId;
        bool changed;
        lock (_gate)
        {
            int before = _pendingUser == user ? _pending.Count : 0;
            changed = before != updates.Count || _pendingUser != user;
            _pending = updates;
            _pendingUser = user;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Checks now, publishes the result and posts the notification when the updates changed.</summary>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <returns>The updates found; empty without a session.</returns>
    public async Task<IReadOnlyList<AppPackageUpdate>> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        if (_currentUser() is not { } user)
        {
            Publish([]);
            return [];
        }

        IAppFileSystemGateway reader = _systemReader();
        AppSourcesConfig sources = await _installer.ReadSourcesAsync(reader, cancellationToken).ConfigureAwait(false);
        AppPackageUpdateReport report = await _installer.FindUpdatesAsync(
                sources,
                await _installer.ReadInstalledAsync(reader, cancellationToken).ConfigureAwait(false),
                await _installer.ReadPersonalInstalledAsync(reader, user.HomePath, cancellationToken).ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        Publish(report.Updates);
        Notify(user, report.Updates);
        return report.Updates;
    }

    /// <summary>Starts the periodic check: now, then every <c>interval-hours</c>, until <see cref="Stop"/>.</summary>
    public void Start()
    {
        CancellationTokenSource loop = new();
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _loop;
            _loop = loop;
        }

        previous?.Cancel();
        previous?.Dispose();
        _ = RunAsync(loop.Token);
    }

    /// <summary>Stops the periodic check (sign-out) and forgets the pending updates.</summary>
    public void Stop()
    {
        CancellationTokenSource? loop;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            _notifiedKey = string.Empty;
        }

        loop?.Cancel();
        loop?.Dispose();
        Publish([]);
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int hours = AppPackageDefaults.DefaultUpdateIntervalHours;
            try
            {
                hours = (await _installer.ReadSourcesAsync(_systemReader(), cancellationToken).ConfigureAwait(false)).UpdateIntervalHours;
                await CheckNowAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is AppPackageException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _logger.LogWarning("apps: update check failed: {Reason}", exception.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(hours), _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Notify(AuthenticatedPrincipal user, IReadOnlyList<AppPackageUpdate> updates)
    {
        string key = string.Join(';', updates.Select(static update => $"{update.AppId}@{update.Available}"));
        lock (_gate)
        {
            if (key == _notifiedKey)
            {
                return;
            }

            _notifiedKey = key;
        }

        if (_notifications is null || updates.Count == 0)
        {
            return;
        }

        Microsoft.Extensions.Localization.IStringLocalizer text = Strings.For(_language());
        _notifications.Enqueue(new Notification(
            NotificationId.FromGuid(Guid.NewGuid()),
            user.UserId,
            StoreAppId,
            NotificationSeverity.Information,
            text["AppUpdates.Notification.Title"],
            updates.Count == 1 ? text["AppUpdates.Notification.One"] : text["AppUpdates.Notification.Many", updates.Count],
            [NotificationAction.LaunchApp(StoreAppId, text["AppUpdates.Notification.View"], [UpdatesPageArgument])],
            _time.GetUtcNow(),
            expiresAtUtc: null));
    }
}
