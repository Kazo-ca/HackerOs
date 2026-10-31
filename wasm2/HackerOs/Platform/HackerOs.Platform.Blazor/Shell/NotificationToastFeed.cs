using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Time;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>One notification shown as a bubble, and when it leaves.</summary>
/// <param name="Notification">The notification, still listed in the clock panel.</param>
/// <param name="HideAtUtc">Simulated time the bubble leaves, unless it is held.</param>
/// <param name="IsHeld">Whether the pointer or the keyboard focus is on it (ADR 0197 §2).</param>
internal sealed record NotificationToast(Notification Notification, DateTimeOffset HideAtUtc, bool IsHeld);

/// <summary>
/// Decides which notifications show as bubbles (ADR 0197): each new notification of the session
/// user shows for <see cref="Lifetime"/>, at most <see cref="MaxVisible"/> at a time (a new one pushes
/// the oldest out). A bubble only hides: the notification stays in the clock panel until dismissed.
/// </summary>
/// <remarks>
/// <see cref="INotificationQueue"/> raises no event, so, like the dot of ADR 0196, the queue is
/// sampled once per simulated second. A notification is "new" the first time a sample sees it;
/// one already older than <see cref="Lifetime"/> then (posted before the shell started, or before
/// this user signed in) is not shown, so a sign-in does not replay the panel as bubbles.
/// </remarks>
internal sealed class NotificationToastFeed : IDisposable
{
    /// <summary>How often the queue is read.</summary>
    public static readonly TimeSpan SamplePeriod = TimeSpan.FromSeconds(1);

    /// <summary>How long a bubble shows when nothing holds it (decision 2 of issue #242: about 6 s).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    /// <summary>The most bubbles shown at once.</summary>
    public const int MaxVisible = 3;

    private readonly INotificationQueue _queue;
    private readonly Func<LocalUserId?> _currentUser;
    private readonly ISimulationClock _clock;
    private readonly HashSet<NotificationId> _seen = [];
    private List<NotificationToast> _toasts = [];
    private LocalUserId? _user;
    private IDisposable? _subscription;
    private bool _isSuppressed;
    private bool _disposed;

    /// <summary>Creates the feed and starts sampling.</summary>
    /// <param name="queue">The OS notification queue.</param>
    /// <param name="currentUser">The session user, or <see langword="null"/> when nobody is signed in.</param>
    /// <param name="clock">The simulation clock: lifetimes and the sampling pace.</param>
    public NotificationToastFeed(INotificationQueue queue, Func<LocalUserId?> currentUser, ISimulationClock clock)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Sample();
    }

    /// <summary>Gets the bubbles to show, oldest first (the newest sits nearest the taskbar).</summary>
    public IReadOnlyList<NotificationToast> Toasts => _toasts;

    /// <summary>Raised when <see cref="Toasts"/> changes; may be raised off the renderer's context.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets or sets whether the panel that lists the notifications is open (ADR 0197 §3): while it
    /// is, no bubble shows, and a notification that arrives meanwhile goes to the open list only.
    /// </summary>
    public bool IsSuppressed
    {
        get => _isSuppressed;
        set
        {
            _isSuppressed = value;
            Refresh();
        }
    }

    /// <summary>Hides one bubble now (its × button); the notification stays in the panel.</summary>
    /// <param name="id">The notification.</param>
    public void Close(NotificationId id) =>
        Apply([.. _toasts.Where(toast => toast.Notification.Id != id)]);

    /// <summary>Stops a bubble's countdown while the pointer or the focus is on it.</summary>
    /// <param name="id">The notification.</param>
    public void Hold(NotificationId id) =>
        Apply([.. _toasts.Select(toast => toast.Notification.Id == id ? toast with { IsHeld = true } : toast)]);

    /// <summary>Restarts a held bubble's countdown, for a full <see cref="Lifetime"/>, once the pointer and the focus left it.</summary>
    /// <param name="id">The notification.</param>
    public void Release(NotificationId id)
    {
        DateTimeOffset hideAt = _clock.UtcNow + Lifetime;
        Apply([.. _toasts.Select(toast => toast.Notification.Id == id && toast.IsHeld
            ? toast with { IsHeld = false, HideAtUtc = hideAt }
            : toast)]);
    }

    /// <summary>Samples the queue now, outside the once-per-second pace.</summary>
    public void Refresh()
    {
        LocalUserId? user = _currentUser();
        if (user != _user)
        {
            // Another session: its notifications are new to this feed, but only recent ones show.
            _user = user;
            _seen.Clear();
        }

        DateTimeOffset now = _clock.UtcNow;
        IReadOnlyList<Notification> active = user is { } current ? _queue.GetActive(current, now) : [];
        HashSet<NotificationId> activeIds = [.. active.Select(static notification => notification.Id)];

        // A notification dismissed or expired in the meantime takes its bubble with it.
        List<NotificationToast> toasts = _isSuppressed
            ? []
            : [.. _toasts.Where(toast => activeIds.Contains(toast.Notification.Id) && (toast.IsHeld || toast.HideAtUtc > now))];

        foreach (Notification notification in active)
        {
            if (!_seen.Add(notification.Id) || _isSuppressed || notification.CreatedAtUtc + Lifetime <= now)
            {
                continue;
            }

            toasts.Add(new NotificationToast(notification, now + Lifetime, IsHeld: false));
        }

        if (toasts.Count > MaxVisible)
        {
            toasts.RemoveRange(0, toasts.Count - MaxVisible);
        }

        _seen.IntersectWith(activeIds);
        Apply(toasts);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _subscription?.Dispose();
    }

    private void Apply(List<NotificationToast> toasts)
    {
        if (toasts.SequenceEqual(_toasts))
        {
            return;
        }

        _toasts = toasts;
        Changed?.Invoke();
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
