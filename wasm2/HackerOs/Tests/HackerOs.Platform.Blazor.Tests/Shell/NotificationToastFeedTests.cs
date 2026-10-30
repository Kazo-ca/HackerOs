using HackerOs.Platform.Blazor.Shell;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Blazor.Tests.Shell;

/// <summary>ADR 0197: which notifications show as bubbles, and for how long.</summary>
public sealed class NotificationToastFeedTests
{
    private readonly ManualSimulationClock _clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
    private readonly InMemoryNotificationQueue _queue = new(maxEntriesPerUser: 20);
    private readonly LocalUserId _alice = LocalUserId.FromGuid(Guid.NewGuid());

    [Fact]
    public void A_new_notification_shows_at_the_next_second_and_raises_changed()
    {
        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);
        int changed = 0;
        feed.Changed += () => changed++;

        NotificationId id = Post(_alice);
        _clock.Advance(1);

        Assert.Equal(id, Assert.Single(feed.Toasts).Notification.Id);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void A_bubble_leaves_after_its_lifetime_but_the_notification_stays_in_the_queue()
    {
        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);
        Post(_alice);
        feed.Refresh();

        _clock.Advance(5);
        Assert.Single(feed.Toasts);

        _clock.Advance(1);
        Assert.Empty(feed.Toasts);
        Assert.Single(_queue.GetActive(_alice, _clock.UtcNow));
    }

    [Fact]
    public void At_most_three_bubbles_show_and_a_new_one_pushes_the_oldest_out()
    {
        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);
        NotificationId first = Post(_alice);
        Post(_alice);
        Post(_alice);
        NotificationId fourth = Post(_alice);
        feed.Refresh();

        Assert.Equal(NotificationToastFeed.MaxVisible, feed.Toasts.Count);
        Assert.DoesNotContain(feed.Toasts, toast => toast.Notification.Id == first);
        Assert.Equal(fourth, feed.Toasts[^1].Notification.Id);
    }

    [Fact]
    public void A_held_bubble_stays_and_restarts_its_countdown_once_released()
    {
        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);
        NotificationId id = Post(_alice);
        feed.Refresh();

        feed.Hold(id);
        _clock.Advance(20);
        Assert.Single(feed.Toasts);

        feed.Release(id);
        _clock.Advance(5);
        Assert.Single(feed.Toasts);
        _clock.Advance(1);
        Assert.Empty(feed.Toasts);
    }

    [Fact]
    public void Closing_a_bubble_keeps_the_notification_and_never_shows_it_again()
    {
        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);
        NotificationId id = Post(_alice);
        feed.Refresh();

        feed.Close(id);
        feed.Refresh();

        Assert.Empty(feed.Toasts);
        Assert.Single(_queue.GetActive(_alice, _clock.UtcNow));
    }

    [Fact]
    public void Dismissing_the_notification_in_the_panel_takes_its_bubble_away()
    {
        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);
        NotificationId id = Post(_alice);
        feed.Refresh();

        _queue.Dismiss(id);
        _clock.Advance(1);

        Assert.Empty(feed.Toasts);
    }

    [Fact]
    public void While_the_panel_is_open_no_bubble_shows_even_after_it_closes()
    {
        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);
        Post(_alice);
        feed.Refresh();

        feed.IsSuppressed = true;
        Assert.Empty(feed.Toasts);
        Post(_alice);
        _clock.Advance(1);
        Assert.Empty(feed.Toasts);

        feed.IsSuppressed = false;
        Assert.Empty(feed.Toasts);
    }

    [Fact]
    public void Notifications_older_than_a_bubble_lifetime_do_not_show_when_the_shell_starts()
    {
        Post(_alice);
        _clock.Advance(10);
        NotificationId recent = Post(_alice);

        using NotificationToastFeed feed = new(_queue, () => _alice, _clock);

        Assert.Equal(recent, Assert.Single(feed.Toasts).Notification.Id);
    }

    [Fact]
    public void Only_the_session_users_notifications_show_and_a_sign_out_clears_the_bubbles()
    {
        LocalUserId? user = _alice;
        using NotificationToastFeed feed = new(_queue, () => user, _clock);
        Post(LocalUserId.FromGuid(Guid.NewGuid()));
        Post(_alice);
        feed.Refresh();
        Assert.Single(feed.Toasts);

        user = null;
        feed.Refresh();
        Assert.Empty(feed.Toasts);
    }

    private NotificationId Post(LocalUserId user)
    {
        NotificationId id = NotificationId.FromGuid(Guid.NewGuid());
        _queue.Enqueue(new Notification(
            id,
            user,
            "org.hackeros.test",
            NotificationSeverity.Information,
            "Title",
            "Message",
            [],
            _clock.UtcNow,
            expiresAtUtc: null));
        return id;
    }
}
