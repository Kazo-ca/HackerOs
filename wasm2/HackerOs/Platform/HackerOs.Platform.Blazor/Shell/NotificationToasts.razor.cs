using HackerOs.App.Abstractions;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Simulation.Abstractions.Notifications;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HackerOs.Platform.Blazor.Shell;

/// <summary>Where a shell shows its notification bubbles (ADR 0197 §1).</summary>
public enum NotificationToastPlacement
{
    /// <summary>Bottom right, above the taskbar of screen 1.</summary>
    AboveTaskbar,

    /// <summary>At the top of a phone screen, like the heads-up notifications of Android and iOS.</summary>
    TopOfScreen,
}

/// <summary>
/// The notification bubbles (toasts) of ADR 0197: every new notification of the OS and of the apps
/// shows here for about 6 s, at most 3 at a time, and stays in the clock panel. The pointer or the
/// keyboard focus on a bubble holds it; its × hides the bubble only; its actions run like in the panel.
/// </summary>
public partial class NotificationToasts : ComponentBase
{
    /// <summary>Prefix of the OS's own notification sources, such as <c>org.hackeros.kernel</c>.</summary>
    private const string SystemSourcePrefix = "org.hackeros.";

    private NotificationToastFeed _feed = null!;

    /// <summary>Gets or sets where the bubbles show.</summary>
    [Parameter]
    public NotificationToastPlacement Placement { get; set; } = NotificationToastPlacement.AboveTaskbar;

    /// <summary>
    /// Gets or sets whether the panel listing the notifications is open: no bubble shows meanwhile
    /// (ADR 0197 §3), the panel already shows what arrives.
    /// </summary>
    [Parameter]
    public bool IsPanelOpen { get; set; }

    private string PlacementClass => Placement == NotificationToastPlacement.TopOfScreen ? "is-top" : "is-above-taskbar";

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _feed = new NotificationToastFeed(NotificationQueue, () => SessionService.CurrentPrincipal?.UserId, SimulationClock);
        _feed.Changed += OnFeedChanged;
    }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (_feed.IsSuppressed != IsPanelOpen)
        {
            _feed.IsSuppressed = IsPanelOpen;
        }
    }

    private void OnFeedChanged() => InvokeAsync(StateHasChanged);

    private static string SeverityClass(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => "is-error",
        NotificationSeverity.Warning => "is-warning",
        NotificationSeverity.Success => "is-success",
        _ => "is-info",
    };

    /// <summary>
    /// The app's name in the session language when the catalog knows it; "HackerOS" for the OS's own
    /// sources (kernel, storage, what's new…), which are no apps; otherwise the raw id.
    /// </summary>
    private string SourceLabel(string sourceAppId)
    {
        if (Services.GetService<AppCatalog>() is { } catalog && catalog.Manifests.TryGetValue(sourceAppId, out AppManifest? manifest))
        {
            System.Globalization.CultureInfo culture = Services.GetService<SessionLocaleService>()?.Culture
                ?? System.Globalization.CultureInfo.CurrentUICulture;
            return catalog.Localize(manifest, culture).Name;
        }

        return sourceAppId.StartsWith(SystemSourcePrefix, StringComparison.Ordinal) ? "HackerOS" : sourceAppId;
    }

    private async Task RunActionAsync(Notification notification, NotificationAction action)
    {
        if (await NotificationActionRunner.RunAsync(Services, notification, action))
        {
            _feed.Close(notification.Id);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _feed.Changed -= OnFeedChanged;
        _feed.Dispose();
    }
}
