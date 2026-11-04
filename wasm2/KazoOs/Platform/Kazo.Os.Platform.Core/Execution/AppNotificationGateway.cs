using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Provides one app instance's authorized notification posting, requiring
/// <see cref="AppCapabilities.NotificationsPost"/>.
/// </summary>
public sealed class AppNotificationGateway : IAppNotificationGateway
{
    private readonly INotificationQueue _queue;
    private readonly ISimulationClock _clock;
    private readonly ICapabilityChecker _capabilities;
    private readonly string _appId;
    private readonly LocalUserId _userId;

    /// <summary>Initializes a notification gateway bound to one app/user.</summary>
    public AppNotificationGateway(
        INotificationQueue queue,
        ISimulationClock clock,
        ICapabilityChecker capabilities,
        string appId,
        LocalUserId userId)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        _appId = appId;
        _userId = userId;
    }

    /// <inheritdoc />
    public NotificationId Post(
        NotificationSeverity severity,
        string title,
        string message,
        IReadOnlyList<NotificationAction>? actions = null,
        TimeSpan? expiresAfter = null)
    {
        _capabilities.Require(AppCapabilities.NotificationsPost);

        DateTimeOffset now = _clock.UtcNow;
        NotificationId id = NotificationId.FromGuid(Guid.NewGuid());
        Notification notification = new(
            id,
            _userId,
            _appId,
            severity,
            title,
            message,
            actions ?? [],
            now,
            expiresAfter is { } delay ? now + delay : null);

        _queue.Enqueue(notification);
        return id;
    }
}
