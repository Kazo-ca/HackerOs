using System.Globalization;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.ServerConnection;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace HackerOs.Platform.Blazor.Sync;

/// <summary>
/// The OS dialog asking which version of a file to keep after a synchronization conflict (ADR 0101
/// section 2), and the shell's anchor for the periodic synchronization of the session.
/// </summary>
/// <remarks>
/// <para>
/// The shell renders exactly one instance above every window. It starts <see cref="SyncScheduler"/>
/// for the logged-in user (the shell only exists after login) and shows <see cref="SyncConflictCenter"/>:
/// the dialog opens by itself on a new conflict and at login when conflicts are still pending.
/// </para>
/// <para>
/// Each file shows the date, device and size of both versions and, on demand, a text preview.
/// <em>Later</em> closes the dialog without choosing: the files stay frozen, a notification says
/// where to find them (Settings → Synchronization), and the dialog comes back at the next login.
/// On a host built without the sync services the component renders nothing.
/// </para>
/// </remarks>
public sealed partial class SyncConflictOverlay
{
    private readonly Dictionary<Guid, SyncConflictPreview> _previews = [];
    private SyncConflictCenter? _center;
    private bool _isBusy;
    private string? _error;

    /// <summary>Gets or sets the dialog's texts, in the session language (ADR 0104).</summary>
    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    /// <summary>Gets or sets the session language, whose <c>LC_TIME</c> culture formats dates and sizes.</summary>
    [Inject]
    private SessionLocaleService Locale { get; set; } = default!;

    /// <summary>Gets or sets the services the optional sync parts are resolved from.</summary>
    [Inject]
    public IServiceProvider Services { get; set; } = default!;

    /// <summary>Converts a time to the session's time zone (ADR 0193), or the device's when the host has none.</summary>
    private DateTimeOffset ToLocal(DateTimeOffset time) =>
        TimeZoneInfo.ConvertTime(time, Services.GetService<SessionTimeZoneService>()?.TimeZone ?? TimeZoneInfo.Local);

    private bool AllRemoteKnown => _center?.Conflicts.All(conflict => conflict.Remote is not null) == true;

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        _center = Services.GetService<SyncConflictCenter>();
        Services.GetService<SyncScheduler>()?.Start();
        if (_center is null)
        {
            return;
        }

        _center.Changed += OnCenterChanged;

        // At login: pending conflicts of an earlier session open the dialog again.
        _ = _center.RefreshAsync(openOnNew: true);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_center is not null)
        {
            _center.Changed -= OnCenterChanged;
            _center.ResetSession();
        }
    }

    private void OnCenterChanged(object? sender, EventArgs args) => _ = InvokeAsync(StateHasChanged);

    private RenderFragment VersionFacts(SyncFileVersion version) => builder =>
    {
        builder.OpenElement(0, "dl");
        builder.AddAttribute(1, "class", "scd-facts");
        if (version.IsDeleted)
        {
            builder.OpenElement(2, "dd");
            builder.AddAttribute(3, "class", "scd-deleted");
            builder.AddContent(4, L["Sync.Deleted"].Value);
            builder.CloseElement();
        }
        else
        {
            AddFact(builder, 10, L["Sync.Modified"], version.ModifiedAtUtc is { } modified
                ? ToLocal(modified).ToString("g", Locale.FormatCulture)
                : L["Sync.Unknown"]);
            AddFact(builder, 20, L["Sync.Size"], version.Length is { } length ? FormatSize(length, L, Locale.FormatCulture) : "—");
        }

        AddFact(builder, 30, L["Sync.Device"], version.DeviceId is { } device && device != Guid.Empty ? device.ToString("N")[..8] : L["Sync.Unknown"]);
        builder.CloseElement();
    };

    private static void AddFact(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder, int sequence, string label, string value)
    {
        builder.OpenElement(sequence, "dd");
        builder.OpenElement(sequence + 1, "span");
        builder.AddAttribute(sequence + 2, "class", "scd-label");
        builder.AddContent(sequence + 3, label);
        builder.CloseElement();
        builder.AddContent(sequence + 4, " " + value);
        builder.CloseElement();
    }

    /// <summary>
    /// Formats a size the way a file manager does: binary units named in the session language
    /// (B/KiB/MiB in English, o/Kio/Mio in French) and the number in the <c>LC_NUMERIC</c>-like
    /// format culture (1.5 or 1,5).
    /// </summary>
    /// <param name="bytes">The size.</param>
    /// <param name="text">The texts holding the unit patterns (<c>Size.Bytes</c>, <c>Size.KiB</c>, <c>Size.MiB</c>).</param>
    /// <param name="format">The culture of the number.</param>
    internal static string FormatSize(long bytes, IStringLocalizer text, CultureInfo format)
    {
        (string key, double value, string number) = bytes switch
        {
            < 1024 => ("Size.Bytes", bytes, "0"),
            < 1024 * 1024 => ("Size.KiB", bytes / 1024d, "0.#"),
            _ => ("Size.MiB", bytes / (1024d * 1024), "0.#")
        };
        return text[key, value.ToString(number, format)];
    }

    private async Task LoadPreviewAsync(Guid recordId)
    {
        if (_center is null)
        {
            return;
        }

        try
        {
            _previews[recordId] = await _center.FileSystemSync.GetConflictPreviewAsync(recordId);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or IOException)
        {
            _previews[recordId] = new SyncConflictPreview(null, null);
        }
    }

    private async Task ResolveAsync(Guid recordId, SyncConflictChoice choice)
    {
        if (_center is null || _isBusy)
        {
            return;
        }

        _isBusy = true;
        _error = null;
        try
        {
            await _center.FileSystemSync.ResolveConflictAsync(recordId, choice);
            _previews.Remove(recordId);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or IOException
                                              or ServerConnectionException or HttpRequestException)
        {
            _error = L["Sync.ResolveFailed", exception.Message];
        }
        finally
        {
            _isBusy = false;
        }

        await _center.RefreshAsync(openOnNew: false);
    }

    private async Task ResolveAllAsync(SyncConflictChoice choice)
    {
        if (_center is null)
        {
            return;
        }

        foreach (Guid recordId in _center.Conflicts.Select(conflict => conflict.RecordId).ToList())
        {
            await ResolveAsync(recordId, choice);
            if (_error is not null)
            {
                return;
            }
        }
    }

    private void Later()
    {
        if (_center is null)
        {
            return;
        }

        int count = _center.Conflicts.Count;
        _center.Close();
        if (Services.GetService<INotificationQueue>() is { } notifications
            && Services.GetService<ISessionService>()?.CurrentPrincipal is { } principal)
        {
            // The conflicts stay pending: say where to find them (ADR 0101 section 2).
            notifications.Enqueue(new Notification(
                NotificationId.FromGuid(Guid.NewGuid()),
                principal.UserId,
                "org.hackeros.sync",
                NotificationSeverity.Warning,
                L["Sync.Pending.Title"],
                count == 1 ? L["Sync.Pending.One"] : L["Sync.Pending.Many", count],
                [],
                DateTimeOffset.UtcNow,
                expiresAtUtc: null));
        }
    }

    private void HandleKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Escape" && !_isBusy)
        {
            Later();
        }
    }
}
