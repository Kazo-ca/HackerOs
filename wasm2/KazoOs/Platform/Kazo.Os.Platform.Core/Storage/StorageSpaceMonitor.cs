using System.Globalization;
using Kazo.Os.Simulation.Abstractions.Metrics;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Storage;

/// <summary>
/// Watches the free space of the host storage (ADR 0099, section 2), like the part of a Linux
/// system that warns about a full disk and keeps <c>ext4</c>'s reserved blocks for root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Measures.</b> Every <see cref="PollInterval"/> while started, and soon after a write asked
/// for one through <see cref="RequestRefresh"/> (never more often than
/// <see cref="MinimumRefreshSpacing"/>), it reads <see cref="IStorageCapacityProbe"/>. The
/// filesystem only reads the cached <see cref="IsRootReserveReached"/>: a write never waits for the
/// browser.
/// </para>
/// <para>
/// <b>Warning.</b> When the storage becomes low (<see cref="StorageSpacePolicy.IsLow"/>), the
/// session user gets one "Low storage space" notification per session, whose action opens the
/// Diagnostic app.
/// </para>
/// <para>
/// <b>Root reserve.</b> Under <see cref="StorageSpacePolicy.ReserveBytes"/> free,
/// <see cref="IsRootReserveReached"/> becomes <see langword="true"/> and the filesystem refuses the
/// writes of a non-administrator with <c>ENOSPC</c>; the system and the administrator keep writing
/// until the browser's real limit.
/// </para>
/// </remarks>
public sealed class StorageSpaceMonitor : IStorageSpaceGuard, IAsyncDisposable
{
    /// <summary>Source app id of the low-space notification.</summary>
    public const string NotificationSourceAppId = "org.hackeros.storage";

    /// <summary>App the low-space notification opens.</summary>
    public const string DiagnosticAppId = "org.hackeros.diagnostic";

    /// <summary>Interval between two periodic measures.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>Shortest interval between two measures asked by <see cref="RequestRefresh"/>.</summary>
    public static readonly TimeSpan MinimumRefreshSpacing = TimeSpan.FromSeconds(5);

    private readonly IStorageCapacityProbe _probe;
    private readonly INotificationQueue? _notifications;
    private readonly Func<AuthenticatedPrincipal?> _currentUser;
    private readonly TimeProvider _time;
    private readonly Func<CultureInfo> _language;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;
    private long _lastRefreshTicks = long.MinValue;
    private LocalUserId? _warnedUser;
    private volatile bool _isRootReserveReached;
    private volatile bool _isLowSpace;
    private StorageCapacity? _last;
    private int _disposed;

    /// <summary>Creates the monitor.</summary>
    /// <param name="probe">Reads the host's usage and quota.</param>
    /// <param name="notifications">Queue receiving the low-space warning; <see langword="null"/> posts none.</param>
    /// <param name="currentUser">Session user who gets the warning (resolved lazily: the session depends on the filesystem).</param>
    /// <param name="time">Time source for the throttle and the poll; defaults to the system clock.</param>
    /// <param name="language">
    /// Language of the session the warning is shown in (ADR 0104: <c>SessionLocaleService.Culture</c>),
    /// read when the warning is posted; English when <see langword="null"/>.
    /// </param>
    public StorageSpaceMonitor(
        IStorageCapacityProbe probe,
        INotificationQueue? notifications = null,
        Func<AuthenticatedPrincipal?>? currentUser = null,
        TimeProvider? time = null,
        Func<CultureInfo>? language = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _language = language ?? (static () => Kazo.Os.AppSdk.Globalization.HackLocale.DefaultLanguage);
        _notifications = notifications;
        _currentUser = currentUser ?? (static () => null);
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public bool IsRootReserveReached => _isRootReserveReached;

    /// <summary>Gets whether the storage was low at the last measure.</summary>
    public bool IsLowSpace => _isLowSpace;

    /// <summary>Gets the last measure, or <see langword="null"/> before the first one or when the host cannot tell.</summary>
    public StorageCapacity? LastCapacity => _last;

    /// <summary>Starts the periodic measures (idempotent); the first one runs at once.</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = RunAsync(_stopping.Token);
    }

    /// <inheritdoc />
    public void RequestRefresh()
    {
        long now = _time.GetTimestamp();
        long last = Interlocked.Read(ref _lastRefreshTicks);
        if (last != long.MinValue && _time.GetElapsedTime(last, now) < MinimumRefreshSpacing)
        {
            return;
        }

        _ = RefreshQuietlyAsync();
    }

    /// <summary>Measures now, updates the reserve state and posts the warning when the storage became low.</summary>
    /// <param name="cancellationToken">Cancels the measure.</param>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _lastRefreshTicks, _time.GetTimestamp());
            StorageCapacity? capacity = await _probe.GetCapacityAsync(cancellationToken).ConfigureAwait(false);
            _last = capacity;
            if (capacity is not { } measured)
            {
                // Unknown quota: nothing is reserved, only the browser's own limit applies.
                _isRootReserveReached = false;
                _isLowSpace = false;
                return;
            }

            _isRootReserveReached = StorageSpacePolicy.IsInRootReserve(measured);
            _isLowSpace = StorageSpacePolicy.IsLow(measured);
            if (_isLowSpace)
            {
                Warn(measured);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The container disposes the instance once per registration (the class and IStorageSpaceGuard).
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }

        _stopping.Dispose();
        _refreshGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await RefreshQuietlyAsync().ConfigureAwait(false);
        using PeriodicTimer timer = new(PollInterval, _time);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RefreshQuietlyAsync().ConfigureAwait(false);
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Stopping, or the host is going away: the last known state stays.
        }
    }

    /// <summary>Posts the warning once per session user (a new login warns again).</summary>
    private void Warn(StorageCapacity capacity)
    {
        if (_notifications is null || _currentUser() is not { } user || _warnedUser == user.UserId)
        {
            return;
        }

        _warnedUser = user.UserId;
        DateTimeOffset now = _time.GetUtcNow();
        Microsoft.Extensions.Localization.IStringLocalizer text = Strings.For(_language());
        _notifications.Enqueue(new Notification(
            NotificationId.FromGuid(Guid.NewGuid()),
            user.UserId,
            NotificationSourceAppId,
            NotificationSeverity.Warning,
            text["Storage.LowSpace.Title"],
            text["Storage.LowSpace.Message", ToMebibytes(capacity.AvailableBytes), ToMebibytes(capacity.QuotaBytes)],
            [NotificationAction.LaunchApp(DiagnosticAppId, text["Storage.LowSpace.OpenDiagnostic"])],
            now,
            expiresAtUtc: null));
    }

    /// <summary>Converts bytes to MiB; the text formats the number (<c>{0:0.#}</c>) in its own culture.</summary>
    private static double ToMebibytes(long bytes) => bytes / (1024.0 * 1024.0);
}
