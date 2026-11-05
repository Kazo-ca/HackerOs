using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Lifecycle;

/// <summary>
/// The notification channel the orchestrator gives one service process (ADR 0097, section 2): each
/// message is applied to that service's unit under that process's PID. A faulty message never
/// reaches the service's code as an exception.
/// </summary>
internal sealed class ServiceNotifyGateway(Action<ServiceNotification> apply) : IAppServiceNotifyGateway
{
    /// <inheritdoc />
    public void Notify(ServiceNotification notification)
    {
        if (notification is null)
        {
            return;
        }

        apply(notification);
    }
}
