using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Notifications;

public sealed class NotificationTests
{
    private static readonly LocalUserId UserId = LocalUserId.FromGuid(Guid.NewGuid());

    private static Notification Create(DateTimeOffset? expiresAtUtc = null) => new(
        NotificationId.FromGuid(Guid.NewGuid()),
        UserId,
        "com.hackeros.mailer",
        NotificationSeverity.Information,
        "Title",
        "Message",
        [],
        DateTimeOffset.UnixEpoch,
        expiresAtUtc);

    [Fact]
    public void IsActive_is_true_before_expiry_and_false_after()
    {
        Notification notification = Create(DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(5));

        Assert.True(notification.IsActive(DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(1)));
        Assert.False(notification.IsActive(DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void A_notification_without_expiry_is_always_active()
    {
        Notification notification = Create(expiresAtUtc: null);

        Assert.True(notification.IsActive(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void Expiry_cannot_precede_creation()
    {
        Assert.Throws<ArgumentException>(() => Create(DateTimeOffset.UnixEpoch - TimeSpan.FromMinutes(1)));
    }
}

public sealed class InMemoryNotificationQueueTests
{
    private static readonly LocalUserId Alice = LocalUserId.FromGuid(Guid.NewGuid());
    private static readonly LocalUserId Bob = LocalUserId.FromGuid(Guid.NewGuid());

    private static Notification Create(LocalUserId userId, string title, DateTimeOffset? expiresAtUtc = null) => new(
        NotificationId.FromGuid(Guid.NewGuid()),
        userId,
        "com.hackeros.mailer",
        NotificationSeverity.Information,
        title,
        "Message",
        [],
        DateTimeOffset.UnixEpoch,
        expiresAtUtc);

    [Fact]
    public void GetActive_only_returns_notifications_scoped_to_the_requested_user()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        queue.Enqueue(Create(Alice, "for-alice"));
        queue.Enqueue(Create(Bob, "for-bob"));

        IReadOnlyList<Notification> aliceNotifications = queue.GetActive(Alice, DateTimeOffset.UnixEpoch);

        Assert.Equal(["for-alice"], aliceNotifications.Select(n => n.Title));
    }

    [Fact]
    public void GetActive_excludes_expired_notifications()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        queue.Enqueue(Create(Alice, "expired", DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(1)));

        IReadOnlyList<Notification> active = queue.GetActive(Alice, DateTimeOffset.UnixEpoch + TimeSpan.FromMinutes(5));

        Assert.Empty(active);
    }

    [Fact]
    public void Dismiss_removes_a_notification_from_GetActive()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);
        Notification notification = Create(Alice, "dismiss-me");
        queue.Enqueue(notification);

        bool dismissed = queue.Dismiss(notification.Id);
        IReadOnlyList<Notification> active = queue.GetActive(Alice, DateTimeOffset.UnixEpoch);

        Assert.True(dismissed);
        Assert.Empty(active);
    }

    [Fact]
    public void Dismiss_returns_false_for_an_unknown_id()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 10);

        Assert.False(queue.Dismiss(NotificationId.FromGuid(Guid.NewGuid())));
    }

    [Fact]
    public void Enqueueing_beyond_capacity_evicts_the_oldest_notification_for_that_user()
    {
        InMemoryNotificationQueue queue = new(maxEntriesPerUser: 2);

        queue.Enqueue(Create(Alice, "first"));
        queue.Enqueue(Create(Alice, "second"));
        queue.Enqueue(Create(Alice, "third"));

        IReadOnlyList<Notification> active = queue.GetActive(Alice, DateTimeOffset.UnixEpoch);

        Assert.Equal(["second", "third"], active.Select(n => n.Title));
    }
}
