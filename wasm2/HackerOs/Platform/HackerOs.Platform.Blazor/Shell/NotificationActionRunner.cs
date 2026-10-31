using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// Runs a notification action chosen in the clock panel or on a notification bubble (ADR 0197 §4),
/// the same way in both places: an action the host registered in <see cref="ShellNotificationActions"/>
/// runs its handler; a launch action (<see cref="NotificationAction.LaunchApp(string, string, IReadOnlyList{string})"/>,
/// used by OS notifications such as the low-storage warning of ADR 0099) launches its app. Either way
/// the notification is then dismissed. Any other action belongs to its app and does nothing here.
/// </summary>
internal static class NotificationActionRunner
{
    /// <summary>Runs the action.</summary>
    /// <param name="services">The shell's services; the registry and the orchestrator may be missing.</param>
    /// <param name="notification">The notification that offers the action.</param>
    /// <param name="action">The chosen action.</param>
    /// <returns><see langword="true"/> when the shell ran it and dismissed the notification.</returns>
    public static async Task<bool> RunAsync(IServiceProvider services, Notification notification, NotificationAction action)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(action);
        INotificationQueue queue = services.GetRequiredService<INotificationQueue>();

        if (services.GetService<ShellNotificationActions>() is { } actions && actions.TryGet(action.Id, out Func<Task>? handler))
        {
            queue.Dismiss(notification.Id);
            await handler();
            return true;
        }

        if (!action.TryGetLaunchedApp(out string? appId)
            || services.GetService<ISessionService>()?.CurrentPrincipal is not AuthenticatedPrincipal principal
            || services.GetService<AppLifecycleOrchestrator>() is not AppLifecycleOrchestrator orchestrator)
        {
            return false;
        }

        AppLaunchResult result = await orchestrator.LaunchAsync(new AppLaunchRequest(appId, principal, [.. action.LaunchArguments]));
        if (!result.IsSuccess)
        {
            return false;
        }

        queue.Dismiss(notification.Id);
        return true;
    }
}
