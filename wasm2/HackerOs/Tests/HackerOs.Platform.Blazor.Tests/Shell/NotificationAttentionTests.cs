using HackerOs.Platform.Blazor.Shell;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Blazor.Tests.Shell;

/// <summary>ADR 0196: the dot of the notification area counts the notifications not looked at yet.</summary>
public sealed class NotificationAttentionTests
{
    private readonly ManualSimulationClock _clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
    private readonly InMemoryNotificationQueue _queue = new(maxEntriesPerUser: 10);
    private readonly LocalUserId _alice = LocalUserId.FromGuid(Guid.NewGuid());

    [Fact]
    public void A_new_notification_is_counted_at_the_next_second_and_raises_changed()
    {
        using NotificationAttention attention = new(_queue, () => _alice, _clock);
        int changed = 0;
        attention.Changed += () => changed++;

        Post(_alice);
        Post(_alice);
        _clock.Advance(1);

        Assert.Equal(2, attention.UnreadCount);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Opening_the_panel_reads_every_notification_but_keeps_them_in_the_list()
    {
        using NotificationAttention attention = new(_queue, () => _alice, _clock);
        Post(_alice);
        attention.Refresh();

        attention.IsViewing = true;
        Post(_alice);
        _clock.Advance(1);
        attention.IsViewing = false;

        Assert.Equal(0, attention.UnreadCount);
        Assert.Equal(2, _queue.GetActive(_alice, _clock.UtcNow).Count);

        Post(_alice);
        attention.Refresh();
        Assert.Equal(1, attention.UnreadCount);
    }

    [Fact]
    public void A_dismissed_notification_is_no_longer_counted()
    {
        using NotificationAttention attention = new(_queue, () => _alice, _clock);
        NotificationId id = Post(_alice);
        attention.Refresh();

        _queue.Dismiss(id);
        attention.Refresh();

        Assert.Equal(0, attention.UnreadCount);
    }

    [Fact]
    public void Only_the_session_user_notifications_count_and_nobody_signed_in_counts_none()
    {
        LocalUserId? user = _alice;
        using NotificationAttention attention = new(_queue, () => user, _clock);
        Post(LocalUserId.FromGuid(Guid.NewGuid()));
        attention.Refresh();
        Assert.Equal(0, attention.UnreadCount);

        Post(_alice);
        user = null;
        attention.Refresh();
        Assert.Equal(0, attention.UnreadCount);
    }

    [Fact]
    public void Disposing_stops_the_sampling()
    {
        NotificationAttention attention = new(_queue, () => _alice, _clock);
        attention.Dispose();

        Post(_alice);
        _clock.Advance(2);

        Assert.Equal(0, attention.UnreadCount);
    }

    private NotificationId Post(LocalUserId user)
    {
        NotificationId id = NotificationId.FromGuid(Guid.NewGuid());
        _queue.Enqueue(new Notification(id, user, "org.hackeros.test", NotificationSeverity.Information, "Title", "Message", [], _clock.UtcNow, expiresAtUtc: null));
        return id;
    }
}
