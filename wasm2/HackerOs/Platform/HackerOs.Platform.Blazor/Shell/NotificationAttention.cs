using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Time;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// Counts the notifications the session user has not looked at yet (ADR 0196): the dot next to the
/// taskbar clock, and on the phone's notification shade handle. Opening the panel that lists them
/// (<see cref="IsViewing"/>) marks every active notification as read; a read notification stays in
/// the list until the user dismisses it, like the notification centre of Windows.
/// </summary>
/// <remarks>
/// <see cref="INotificationQueue"/> raises no event, so the count is sampled once per simulated
/// second, the same pace as the taskbar clock, and <see cref="Changed"/> is raised only when it moves.
/// The read state lives in memory for the page: a notification is posted for one page anyway.
/// </remarks>
internal sealed class NotificationAttention : IDisposable
{
    private static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(1);

    private readonly INotificationQueue _queue;
    private readonly Func<LocalUserId?> _currentUser;
    private readonly ISimulationClock _clock;
    private readonly HashSet<NotificationId> _read = [];
    private IDisposable? _subscription;
    private bool _isViewing;
    private bool _disposed;

    /// <summary>Creates the counter and starts sampling.</summary>
    /// <param name="queue">The OS notification queue.</param>
    /// <param name="currentUser">The session user, or <see langword="null"/> when nobody is signed in.</param>
    /// <param name="clock">The simulation clock: expiry and the sampling pace.</param>
    public NotificationAttention(INotificationQueue queue, Func<LocalUserId?> currentUser, ISimulationClock clock)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Sample();
    }

    /// <summary>Gets the number of active notifications the user has not looked at yet.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>Raised when <see cref="UnreadCount"/> changes; may be raised off the renderer's context.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets or sets whether the panel that lists the notifications is open. While it is, every
    /// notification it shows, including one that arrives meanwhile, counts as read.
    /// </summary>
    public bool IsViewing
    {
        get => _isViewing;
        set
        {
            _isViewing = value;
            Refresh();
        }
    }

    /// <summary>Samples the queue now, outside the once-per-second pace.</summary>
    public void Refresh()
    {
        IReadOnlyList<Notification> active = _currentUser() is { } user
            ? _queue.GetActive(user, _clock.UtcNow)
            : [];
        if (_isViewing)
        {
            foreach (Notification notification in active)
            {
                _read.Add(notification.Id);
            }
        }

        // Forget the ids that left the queue, so the set stays as small as the queue.
        _read.IntersectWith(active.Select(static notification => notification.Id));
        int count = active.Count(notification => !_read.Contains(notification.Id));
        if (count != UnreadCount)
        {
            UnreadCount = count;
            Changed?.Invoke();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _subscription?.Dispose();
    }

    private void Sample()
    {
        if (_disposed)
        {
            return;
        }

        Refresh();
        _subscription?.Dispose();
        _subscription = _clock.Schedule(SamplePeriod, Sample);
    }
}
