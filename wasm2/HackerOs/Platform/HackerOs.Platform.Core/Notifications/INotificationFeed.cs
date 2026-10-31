using HackerOs.Simulation.Abstractions.Notifications;

namespace HackerOs.Platform.Core.Notifications;

/// <summary>
/// Tells the shell that a notification was posted, whoever posted it (an app, the OOM killer, the
/// storage monitor). The sound service plays the notification sound from it (ADR 0204 section 2).
/// </summary>
public interface INotificationFeed
{
    /// <summary>Raised after a notification was queued, on the thread that posted it.</summary>
    event Action<Notification>? Posted;
}
