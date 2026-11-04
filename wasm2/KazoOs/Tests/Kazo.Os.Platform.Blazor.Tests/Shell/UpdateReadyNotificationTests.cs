using Kazo.Os.Platform.Blazor.Shell;
using Kazo.Os.Platform.Core.Notifications;
using Kazo.Os.Platform.Core.Time;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.Blazor.Tests.Shell;

/// <summary>ADR 0197 §4-5: « Update ready » as a notification, and the actions the shell runs.</summary>
public sealed class UpdateReadyNotificationTests
{
    private readonly ManualSimulationClock _clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
    private readonly InMemoryNotificationQueue _queue = new(maxEntriesPerUser: 20);
    private readonly ShellNotificationActions _actions = new();
    private readonly LocalUserId _alice = LocalUserId.FromGuid(Guid.NewGuid());

    [Fact]
    public void It_is_posted_once_per_user_never_expires_and_offers_restart()
    {
        using UpdateReadyNotification update = new(_queue, _clock, _actions, () => Task.CompletedTask);

        Assert.True(update.Show(_alice, "Ready", "Apps will close.", "Restart"));
        Assert.False(update.Show(_alice, "Ready", "Apps will close.", "Restart"));

        Notification notification = Assert.Single(_queue.GetActive(_alice, _clock.UtcNow.AddYears(1)));
        Assert.Null(notification.ExpiresAtUtc);
        Assert.Equal(UpdateReadyNotification.SourceAppId, notification.SourceAppId);
        Assert.Equal(UpdateReadyNotification.RestartActionId, Assert.Single(notification.Actions).Id);
    }

    [Fact]
    public void A_dismissed_notification_is_not_posted_again_but_a_new_version_replaces_it()
    {
        using UpdateReadyNotification update = new(_queue, _clock, _actions, () => Task.CompletedTask);
        update.Show(_alice, "Ready", "Apps will close.", "Restart");
        _queue.Dismiss(_queue.GetActive(_alice, _clock.UtcNow)[0].Id);

        Assert.False(update.Show(_alice, "Ready", "Apps will close.", "Restart"));
        Assert.Empty(_queue.GetActive(_alice, _clock.UtcNow));

        Assert.True(update.Show(_alice, "Kazo OS 9.0 is available.", "Apps will close.", "Restart"));
        Assert.Equal("Kazo OS 9.0 is available.", Assert.Single(_queue.GetActive(_alice, _clock.UtcNow)).Title);
    }

    [Fact]
    public async Task Restart_runs_the_hosts_handler_and_dismisses_the_notification()
    {
        int restarts = 0;
        using UpdateReadyNotification update = new(_queue, _clock, _actions, () =>
        {
            restarts++;
            return Task.CompletedTask;
        });
        update.Show(_alice, "Ready", "Apps will close.", "Restart");
        Notification notification = _queue.GetActive(_alice, _clock.UtcNow)[0];
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<INotificationQueue>(_queue)
            .AddSingleton(_actions)
            .BuildServiceProvider();

        Assert.True(await NotificationActionRunner.RunAsync(services, notification, notification.Actions[0]));

        Assert.Equal(1, restarts);
        Assert.Empty(_queue.GetActive(_alice, _clock.UtcNow));
    }

    [Fact]
    public void A_failed_download_posts_one_warning_with_restart_to_retry()
    {
        using UpdateReadyNotification update = new(_queue, _clock, _actions, () => Task.CompletedTask);
        update.Show(_alice, "Ready", "Apps will close.", "Restart");

        update.ShowDownloadFailed(_alice, "Ready", "Could not download.", "Restart");
        update.ShowDownloadFailed(_alice, "Ready", "Could not download.", "Restart");

        Notification warning = Assert.Single(
            _queue.GetActive(_alice, _clock.UtcNow),
            notification => notification.Severity == NotificationSeverity.Warning);
        Assert.Equal("Could not download.", warning.Message);
        Assert.Equal(UpdateReadyNotification.RestartActionId, Assert.Single(warning.Actions).Id);
        Assert.Equal(2, _queue.GetActive(_alice, _clock.UtcNow).Count);
    }

    [Fact]
    public async Task An_action_nobody_registered_belongs_to_its_app()
    {
        Notification notification = new(
            NotificationId.FromGuid(Guid.NewGuid()),
            _alice,
            "org.example.app",
            NotificationSeverity.Information,
            "Title",
            "Message",
            [new NotificationAction("reply", "Reply")],
            _clock.UtcNow,
            expiresAtUtc: null);
        _queue.Enqueue(notification);
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<INotificationQueue>(_queue)
            .AddSingleton(_actions)
            .BuildServiceProvider();

        Assert.False(await NotificationActionRunner.RunAsync(services, notification, notification.Actions[0]));
        Assert.Single(_queue.GetActive(_alice, _clock.UtcNow));
    }

    [Fact]
    public void Without_shell_actions_no_restart_is_offered_and_disposing_unregisters_it()
    {
        using (UpdateReadyNotification bare = new(_queue, _clock, actions: null, () => Task.CompletedTask))
        {
            bare.Show(_alice, "Ready", "Apps will close.", "Restart");
            Assert.Empty(_queue.GetActive(_alice, _clock.UtcNow)[0].Actions);
        }

        UpdateReadyNotification update = new(_queue, _clock, _actions, () => Task.CompletedTask);
        Assert.True(_actions.TryGet(UpdateReadyNotification.RestartActionId, out _));
        update.Dispose();
        Assert.False(_actions.TryGet(UpdateReadyNotification.RestartActionId, out _));
    }
}
