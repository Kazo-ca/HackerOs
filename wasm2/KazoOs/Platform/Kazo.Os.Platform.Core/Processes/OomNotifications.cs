using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Time;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core.Processes;

/// <summary>What the OOM killer tells the user (ADR 0126, sections 3 and 5).</summary>
public interface IOomNotifier
{
    /// <summary>A window app was killed: its window vanished, the user is told why.</summary>
    /// <param name="process">The killed process.</param>
    void ProcessKilled(ProcessRecord process);

    /// <summary>A runtime of the page passed 80 % of the browser's real memory limit (once per runtime).</summary>
    /// <param name="runtime">The runtime.</param>
    void RuntimeNearLimit(RuntimeMemoryReading runtime);
}

/// <summary>
/// Posts the OOM killer's notifications to the notification queue, in the session's language
/// (ADR 0104): "“Hack Paint” was closed: out of memory", and the warning that the page nears the
/// browser's memory limit.
/// </summary>
public sealed class NotificationQueueOomNotifier : IOomNotifier
{
    /// <summary>Source app id of the notifications, the kernel's.</summary>
    public const string SourceAppId = "org.hackeros.kernel";

    private const double Mebibyte = 1024 * 1024;

    private readonly INotificationQueue _notifications;
    private readonly ISimulationClock _clock;
    private readonly Func<AppCatalog?> _catalog;
    private readonly Func<AuthenticatedPrincipal?> _currentUser;
    private readonly Func<CultureInfo> _language;

    /// <summary>Creates the notifier.</summary>
    /// <param name="notifications">The queue.</param>
    /// <param name="clock">Simulated clock of the notifications' time.</param>
    /// <param name="catalog">Lazily resolves the catalog that names apps; without it, the app id is shown.</param>
    /// <param name="currentUser">The session user, who gets the browser warning.</param>
    /// <param name="language">The session's language; English when <see langword="null"/>.</param>
    public NotificationQueueOomNotifier(
        INotificationQueue notifications,
        ISimulationClock clock,
        Func<AppCatalog?>? catalog = null,
        Func<AuthenticatedPrincipal?>? currentUser = null,
        Func<CultureInfo>? language = null)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _catalog = catalog ?? (static () => null);
        _currentUser = currentUser ?? (static () => null);
        _language = language ?? (static () => Kazo.Os.AppSdk.Globalization.HackLocale.DefaultLanguage);
    }

    /// <inheritdoc />
    public void ProcessKilled(ProcessRecord process)
    {
        ArgumentNullException.ThrowIfNull(process);
        CultureInfo culture = _language();
        IStringLocalizer text = Strings.For(culture);
        string name = _catalog() is { } catalog && catalog.Manifests.TryGetValue(process.AppId, out AppManifest? manifest)
            ? catalog.Localize(manifest, culture).Name
            : process.AppId;
        Post(process.UserId, NotificationSeverity.Error, text["Oom.Killed.Title"], text["Oom.Killed.Message", name]);
    }

    /// <inheritdoc />
    public void RuntimeNearLimit(RuntimeMemoryReading runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (_currentUser() is not { } user)
        {
            return;
        }

        IStringLocalizer text = Strings.For(_language());
        Post(user.UserId, NotificationSeverity.Warning, text["Oom.BrowserLimit.Title"],
            text["Oom.BrowserLimit.Message", runtime.Name, runtime.UsedBytes / Mebibyte, runtime.CeilingBytes / Mebibyte]);
    }

    private void Post(LocalUserId user, NotificationSeverity severity, string title, string message) =>
        _notifications.Enqueue(new Notification(
            NotificationId.FromGuid(Guid.NewGuid()),
            user,
            SourceAppId,
            severity,
            title,
            message,
            [],
            _clock.UtcNow,
            expiresAtUtc: null));
}
