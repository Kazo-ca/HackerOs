using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Notifications;

/// <summary>
/// In-memory <see cref="INotificationQueue"/> that retains at most <see cref="MaxEntriesPerUser"/>
/// notifications per user, evicting the oldest entry for that user once full.
/// </summary>
public sealed class InMemoryNotificationQueue : INotificationQueue, INotificationFeed
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LocalUserId, List<Notification>> _notificationsByUser = [];
    private readonly HashSet<NotificationId> _dismissedIds = [];

    /// <summary>Initializes an in-memory notification queue.</summary>
    /// <param name="maxEntriesPerUser">Maximum number of notifications retained per user; must be at least one.</param>
    public InMemoryNotificationQueue(int maxEntriesPerUser)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntriesPerUser, 1);

        MaxEntriesPerUser = maxEntriesPerUser;
    }

    /// <summary>Gets the maximum number of notifications retained per user.</summary>
    public int MaxEntriesPerUser { get; }

    /// <inheritdoc />
    public event Action<Notification>? Posted;

    /// <inheritdoc />
    public void Enqueue(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        lock (_gate)
        {
            if (!_notificationsByUser.TryGetValue(notification.UserId, out List<Notification>? notifications))
            {
                notifications = [];
                _notificationsByUser[notification.UserId] = notifications;
            }

            notifications.Add(notification);
            if (notifications.Count > MaxEntriesPerUser)
            {
                Notification evicted = notifications[0];
                notifications.RemoveAt(0);
                _dismissedIds.Remove(evicted.Id);
            }
        }

        // Outside the lock: a listener (the sound service, ADR 0204) may read the queue back.
        Posted?.Invoke(notification);
    }

    /// <inheritdoc />
    public IReadOnlyList<Notification> GetActive(LocalUserId userId, DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (!_notificationsByUser.TryGetValue(userId, out List<Notification>? notifications))
            {
                return [];
            }

            return [.. notifications.Where(n => n.IsActive(nowUtc) && !_dismissedIds.Contains(n.Id))];
        }
    }

    /// <inheritdoc />
    public bool Dismiss(NotificationId id)
    {
        lock (_gate)
        {
            foreach (List<Notification> notifications in _notificationsByUser.Values)
            {
                if (notifications.Any(n => n.Id == id))
                {
                    return _dismissedIds.Add(id);
                }
            }

            return false;
        }
    }
}
