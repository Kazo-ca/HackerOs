using System.Globalization;
using System.Text.Json;
using HackerOs.AppSdk.WhatsNew;
using HackerOs.Platform.Core.Settings;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace HackerOs.Platform.Core.WhatsNew;

/// <summary>
/// What changed since the session user's last visit (ADR 0186 §4, §5): the served change list and,
/// per account, the ids already seen, kept in <c>~/.local/state/hackeros/whats-new.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>State file.</b> <c>{ "seen": [ "&lt;id&gt;", … ] }</c>, written for the session user through
/// <see cref="IOsSettingsService"/>. Ids that left the list stay in the file: an entry never comes
/// back as new.
/// </para>
/// <para>
/// <b>New account.</b> When the file does not exist yet (or cannot be read as a list), everything is
/// marked seen except the entries of the latest published version and those of the next version
/// already deployed (<see cref="WhatsNewCatalog.LatestIds"/>), and the file is written at once. Without
/// a served list (offline), nothing is written, so the rule waits for a list.
/// </para>
/// <para>
/// <b>Sign-in notification.</b> <see cref="NotifyNewChangesAsync"/>, called by the shell once the
/// desktop shows, posts « 3 new changes since your last visit » with a <b>View</b> action that opens
/// Settings ▸ What's new, once per account and page (ADR 0186 §4.5).
/// </para>
/// </remarks>
public sealed class WhatsNewService
{
    /// <summary>The account's state file: <c>~/.local/state/hackeros/whats-new.json</c>.</summary>
    public static readonly OsSettingsFile StateFile = new(OsSettingsLocation.UserState, "whats-new.json");

    /// <summary>Source app id of the sign-in notification.</summary>
    public const string NotificationSourceAppId = "org.hackeros.whats-new";

    /// <summary>App the notification opens.</summary>
    public const string SettingsAppId = "org.hackeros.settings";

    /// <summary>Argument that opens the What's new page of Settings.</summary>
    public const string SettingsPageArgument = "--page=whats-new";

    private readonly IWhatsNewSource _source;
    private readonly IOsSettingsService _settings;
    private readonly ILogger? _logger;
    private readonly INotificationQueue? _notifications;
    private readonly Func<AuthenticatedPrincipal?> _currentUser;
    private readonly Func<CultureInfo> _language;
    private readonly TimeProvider _time;
    private readonly HashSet<LocalUserId> _notifiedUsers = [];
    private readonly Dictionary<LocalUserId, NotificationId> _postedNotifications = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhatsNewCatalog? _catalog;
    private string? _stateHome;
    private HashSet<string>? _seen;

    /// <summary>Creates the service.</summary>
    /// <param name="source">Reads the served <c>whats-new.json</c>.</param>
    /// <param name="settings">Reads and writes the session user's state file.</param>
    /// <param name="logger">Receives unreadable-state warnings; optional.</param>
    /// <param name="notifications">Queue receiving the sign-in notification; <see langword="null"/> posts none.</param>
    /// <param name="currentUser">Session user who gets it (resolved lazily).</param>
    /// <param name="language">Language of the session (ADR 0104), read when it is posted; English when <see langword="null"/>.</param>
    /// <param name="time">Time of the notification; defaults to the system clock.</param>
    public WhatsNewService(
        IWhatsNewSource source,
        IOsSettingsService settings,
        ILogger<WhatsNewService>? logger = null,
        INotificationQueue? notifications = null,
        Func<AuthenticatedPrincipal?>? currentUser = null,
        Func<CultureInfo>? language = null,
        TimeProvider? time = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;
        _notifications = notifications;
        _currentUser = currentUser ?? (static () => null);
        _language = language ?? (static () => HackerOs.AppSdk.Globalization.HackLocale.DefaultLanguage);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after the changes seen by the session user changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the served change list. It is read once per page: a new deployment reloads the page
    /// (ADR 0174). An unreachable list is an empty one, read again at the next call.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<WhatsNewCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (_catalog is { } cached)
        {
            return cached;
        }

        string? document;
        try
        {
            document = await _source.GetDocumentAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning("the change list could not be read: {Error}", exception.Message);
            return WhatsNewCatalog.Empty;
        }

        if (document is null)
        {
            return WhatsNewCatalog.Empty;
        }

        WhatsNewCatalog catalog = WhatsNewCatalog.Parse(document);
        _catalog = catalog;
        return catalog;
    }

    /// <summary>Gets the change list of the session user in <paramref name="culture"/>'s language, with its new entries.</summary>
    /// <param name="culture">The language of the texts.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<HackChangeList> GetAsync(CultureInfo culture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(culture);
        WhatsNewCatalog catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> seen = await GetSeenAsync(catalog, cancellationToken).ConfigureAwait(false);
        List<HackChangeGroup> groups = [];
        int newCount = 0;
        foreach (WhatsNewCatalogGroup group in catalog.Groups)
        {
            List<HackChangeEntry> entries = [];
            foreach (WhatsNewCatalogEntry entry in group.Entries)
            {
                bool isNew = !seen.Contains(entry.Id);
                newCount += isNew ? 1 : 0;
                entries.Add(entry.Localize(culture, isNew));
            }

            groups.Add(new HackChangeGroup(group.Version, group.Since, group.Date, entries));
        }

        return new HackChangeList(groups, newCount);
    }

    /// <summary>Gets the number of entries the session user has not seen (the Start menu dot, the sign-in notification).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async ValueTask<int> GetNewCountAsync(CancellationToken cancellationToken = default)
    {
        WhatsNewCatalog catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> seen = await GetSeenAsync(catalog, cancellationToken).ConfigureAwait(false);
        return catalog.Entries.Count(entry => !seen.Contains(entry.Id));
    }

    /// <summary>Marks entries as seen by the session user and writes the state file.</summary>
    /// <param name="ids">The ids; unknown ones are kept too (an entry that is not served yet).</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async ValueTask MarkSeenAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        WhatsNewCatalog catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<string> seen = await LoadSeenLockedAsync(catalog, cancellationToken).ConfigureAwait(false);
            int before = seen.Count;
            seen.UnionWith(ids.Where(static id => !string.IsNullOrWhiteSpace(id)));
            changed = seen.Count != before;
            if (changed)
            {
                await WriteLockedAsync(seen, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            await DismissNotificationWhenAllSeenAsync(cancellationToken).ConfigureAwait(false);
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Posts the sign-in notification when the session user has new entries: once per account for
    /// the life of the page, whether or not there was anything to say.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="true"/> when a notification was posted.</returns>
    public async ValueTask<bool> NotifyNewChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_notifications is null || _currentUser() is not { } user)
        {
            return false;
        }

        lock (_notifiedUsers)
        {
            if (!_notifiedUsers.Add(user.UserId))
            {
                return false;
            }
        }

        int count = await GetNewCountAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            return false;
        }

        Microsoft.Extensions.Localization.IStringLocalizer text = Strings.For(_language());
        NotificationId id = NotificationId.FromGuid(Guid.NewGuid());
        lock (_notifiedUsers)
        {
            _postedNotifications[user.UserId] = id;
        }

        _notifications.Enqueue(new Notification(
            id,
            user.UserId,
            NotificationSourceAppId,
            NotificationSeverity.Information,
            text["WhatsNew.Notification.Title"],
            count == 1 ? text["WhatsNew.Notification.One"] : text["WhatsNew.Notification.Many", count],
            [NotificationAction.LaunchApp(SettingsAppId, text["WhatsNew.Notification.View"], [SettingsPageArgument])],
            _time.GetUtcNow(),
            expiresAtUtc: null));
        return true;
    }

    /// <summary>
    /// Dismisses the sign-in notification once the session user has seen every change, wherever it
    /// was seen (ADR 0196 §4): it has nothing left to say, so it leaves the clock's list and dot.
    /// </summary>
    private async ValueTask DismissNotificationWhenAllSeenAsync(CancellationToken cancellationToken)
    {
        if (_notifications is null || _currentUser() is not { } user)
        {
            return;
        }

        NotificationId id;
        lock (_notifiedUsers)
        {
            if (!_postedNotifications.TryGetValue(user.UserId, out id))
            {
                return;
            }
        }

        if (await GetNewCountAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return;
        }

        lock (_notifiedUsers)
        {
            _postedNotifications.Remove(user.UserId);
        }

        _notifications.Dismiss(id);
    }

    private async ValueTask<IReadOnlySet<string>> GetSeenAsync(WhatsNewCatalog catalog, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new HashSet<string>(await LoadSeenLockedAsync(catalog, cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<HashSet<string>> LoadSeenLockedAsync(WhatsNewCatalog catalog, CancellationToken cancellationToken)
    {
        string? home = _settings.CurrentHomeDirectory;
        if (home is null)
        {
            // No session: nothing is new (and nothing is written).
            return [.. catalog.Entries.Select(static entry => entry.Id)];
        }

        if (_seen is { } cached && string.Equals(_stateHome, home, StringComparison.Ordinal))
        {
            return cached;
        }

        HashSet<string>? seen = null;
        try
        {
            seen = ParseState(await _settings.ReadFileAsync(StateFile, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            _logger?.LogWarning("the what's new state could not be read: {Error}", exception.Message);
        }

        if (seen is null)
        {
            if (!catalog.Entries.Any())
            {
                // Offline or no list: wait for one before applying the new-account rule.
                return [];
            }

            IReadOnlySet<string> latest = catalog.LatestIds;
            seen = [.. catalog.Entries.Select(static entry => entry.Id).Where(id => !latest.Contains(id))];
            await WriteLockedAsync(seen, cancellationToken).ConfigureAwait(false);
        }

        _stateHome = home;
        _seen = seen;
        return seen;
    }

    private async ValueTask WriteLockedAsync(HashSet<string> seen, CancellationToken cancellationToken)
    {
        try
        {
            await _settings.WriteFileAsync(StateFile, SerializeState(seen), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            // The badges still go for this visit; they come back at the next one.
            _logger?.LogWarning("the what's new state could not be written: {Error}", exception.Message);
        }
    }

    /// <summary>Reads a state file: the ids of <c>seen</c>, or <see langword="null"/> when the file is missing or unreadable.</summary>
    internal static HashSet<string>? ParseState(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using JsonDocument json = JsonDocument.Parse(content);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("seen", out JsonElement ids)
                || ids.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return [.. ids.EnumerateArray()
                .Where(static id => id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                .Select(static id => id.GetString()!)];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a state file, ids sorted so the file only changes when the list does.</summary>
    internal static string SerializeState(IEnumerable<string> seen)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("seen");
            foreach (string id in seen.Order(StringComparer.Ordinal))
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }
}
