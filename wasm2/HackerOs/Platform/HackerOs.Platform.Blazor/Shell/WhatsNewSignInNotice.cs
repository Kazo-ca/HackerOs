using HackerOs.Platform.Core.WhatsNew;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>
/// The sign-in notification of the change list (ADR 0186 §4.5): « 3 new changes since your last
/// visit », posted once per account by <see cref="WhatsNewService"/> when the desktop or the phone
/// shell shows. The notification area then carries the dot of unread notifications (ADR 0196).
/// Does nothing on a host that serves no change list.
/// </summary>
internal static class WhatsNewSignInNotice
{
    /// <summary>Posts the sign-in notification when the session user has unseen changes.</summary>
    /// <param name="services">The shell's services; <see cref="WhatsNewService"/> may be missing.</param>
    public static async Task PostAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<WhatsNewService>() is not { } service)
        {
            return;
        }

        try
        {
            await service.NotifyNewChangesAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException)
        {
            // No notification: the Settings page still lists the changes.
        }
    }
}
