using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using HackerOs.Simulation.Abstractions.Time;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// « Update ready » inside a session (ADR 0197 §5, ADR 0162, ADR 0174): a notification that never
/// expires, with « Redémarrer », posted once per signed-in user. It shows as a bubble when posted and
/// stays in the clock panel until the user dismisses it or restarts. The sign-in screens have no
/// session and no panel: the host keeps its own notice there.
/// </summary>
public sealed class UpdateReadyNotification : IDisposable
{
    /// <summary>Source of the notification: the OS's update, which no app owns.</summary>
    public const string SourceAppId = "org.hackeros.update";

    /// <summary>Id of the « Redémarrer » action, which the shell runs through <see cref="ShellNotificationActions"/>.</summary>
    public const string RestartActionId = "hackeros.update.restart";

    private readonly INotificationQueue _queue;
    private readonly ISimulationClock _clock;
    private readonly IDisposable? _registration;
    private readonly Dictionary<LocalUserId, (NotificationId Id, string Message)> _posted = [];

    /// <summary>Creates the poster and registers what « Redémarrer » does.</summary>
    /// <param name="queue">The OS notification queue.</param>
    /// <param name="clock">The simulation clock: the notification's time.</param>
    /// <param name="actions">The shell's actions; without it, « Redémarrer » is not offered.</param>
    /// <param name="restart">Restarts the OS on the new build.</param>
    public UpdateReadyNotification(
        INotificationQueue queue,
        ISimulationClock clock,
        ShellNotificationActions? actions,
        Func<Task> restart)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ArgumentNullException.ThrowIfNull(restart);
        _registration = actions?.Register(RestartActionId, restart);
    }

    /// <summary>
    /// Posts the notification for <paramref name="user"/>, once: calling it again with the same text
    /// does nothing, even after the user dismissed it. A new text (the version became known) replaces
    /// the previous notification.
    /// </summary>
    /// <param name="user">The signed-in user.</param>
    /// <param name="title">The title, such as « HackerOS 2.2.0 est disponible. ».</param>
    /// <param name="message">The body, such as « Les apps ouvertes seront fermées. ».</param>
    /// <param name="restartLabel">The label of « Redémarrer ».</param>
    /// <returns><see langword="true"/> when a notification was posted.</returns>
    public bool Show(LocalUserId user, string title, string message, string restartLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(restartLabel);
        string text = title + "\n" + message;
        if (_posted.TryGetValue(user, out (NotificationId Id, string Message) previous))
        {
            if (previous.Message == text)
            {
                return false;
            }

            _queue.Dismiss(previous.Id);
        }

        NotificationId id = NotificationId.FromGuid(Guid.NewGuid());
        IReadOnlyList<NotificationAction> actions = _registration is null ? [] : [new NotificationAction(RestartActionId, restartLabel)];
        _queue.Enqueue(new Notification(
            id,
            user,
            SourceAppId,
            NotificationSeverity.Information,
            title,
            message,
            actions,
            _clock.UtcNow,
            expiresAtUtc: null));
        _posted[user] = (id, text);
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _registration?.Dispose();
}
