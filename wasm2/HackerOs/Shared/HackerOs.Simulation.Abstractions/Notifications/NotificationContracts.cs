using System.Globalization;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Simulation.Abstractions.Notifications;

/// <summary>Identifies one notification independently of its content.</summary>
public readonly record struct NotificationId
{
    private NotificationId(Guid value) => Value = value;

    /// <summary>Gets the opaque 128-bit identifier value.</summary>
    public Guid Value { get; }

    /// <summary>Creates a notification identifier from a non-empty value.</summary>
    public static NotificationId FromGuid(Guid value) => value == Guid.Empty
        ? throw new ArgumentException("Notification IDs cannot be empty.", nameof(value))
        : new NotificationId(value);

    /// <summary>Returns canonical lowercase identifier text.</summary>
    public override string ToString() => Value.ToString("N", CultureInfo.InvariantCulture);
}

/// <summary>Defines the visual urgency of one notification, without dictating rendering.</summary>
public enum NotificationSeverity
{
    /// <summary>Routine, informational notice.</summary>
    Information,

    /// <summary>An operation completed successfully.</summary>
    Success,

    /// <summary>An unexpected but recoverable condition.</summary>
    Warning,

    /// <summary>An operation failed.</summary>
    Error
}

/// <summary>Represents one user-actionable choice offered by a notification.</summary>
public sealed record NotificationAction
{
    /// <summary>Initializes a validated notification action.</summary>
    /// <param name="id">Non-empty, app-defined action identifier delivered back to the app when chosen.</param>
    /// <param name="label">Non-empty human-readable label for the action.</param>
    /// <exception cref="ArgumentException">The ID or label is empty.</exception>
    public NotificationAction(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("An action ID is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("An action label is required.", nameof(label));
        }

        Id = id;
        Label = label;
    }

    /// <summary>Gets the app-defined action identifier delivered back to the app when chosen.</summary>
    public string Id { get; }

    /// <summary>Gets the human-readable label for the action.</summary>
    public string Label { get; }

    /// <summary>Prefix of an action the shell handles itself by launching the named app.</summary>
    public const string LaunchAppPrefix = "launch:";

    /// <summary>
    /// Creates an action the shell handles itself: choosing it launches <paramref name="appId"/>
    /// and dismisses the notification. Used by OS notifications, such as the low-storage warning
    /// that opens Diagnostic (ADR 0099, section 2).
    /// </summary>
    /// <param name="appId">App launched when the action is chosen.</param>
    /// <param name="label">Label of the action.</param>
    public static NotificationAction LaunchApp(string appId, string label) => LaunchApp(appId, label, []);

    /// <summary>
    /// Creates an action that launches <paramref name="appId"/> with <paramref name="arguments"/>,
    /// such as the sign-in notification that opens Settings ▸ What's new with
    /// <c>--page=whats-new</c> (ADR 0186 §4.5).
    /// </summary>
    /// <param name="appId">App launched when the action is chosen.</param>
    /// <param name="label">Label of the action.</param>
    /// <param name="arguments">Its command line; no argument may hold <see cref="ArgumentSeparator"/>.</param>
    public static NotificationAction LaunchApp(string appId, string label, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(arguments);
        if (appId.Contains(ArgumentSeparator, StringComparison.Ordinal)
            || arguments.Any(static argument => argument is null || argument.Contains(ArgumentSeparator, StringComparison.Ordinal)))
        {
            throw new ArgumentException("An app id or argument cannot hold the argument separator.", nameof(arguments));
        }

        return new NotificationAction(
            LaunchAppPrefix + appId + string.Concat(arguments.Select(static argument => ArgumentSeparator + argument)),
            label);
    }

    /// <summary>Separates the app id and the arguments of a launch action's <see cref="Id"/> (the unit separator, U+001F).</summary>
    public const char ArgumentSeparator = '\u001F';

    /// <summary>Gets the app this action launches, when it was made by <see cref="LaunchApp(string, string)"/>.</summary>
    /// <param name="appId">The app id, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a launch action.</returns>
    public bool TryGetLaunchedApp([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? appId)
    {
        appId = Id.StartsWith(LaunchAppPrefix, StringComparison.Ordinal) && Id.Length > LaunchAppPrefix.Length
            ? Id[LaunchAppPrefix.Length..].Split(ArgumentSeparator)[0]
            : null;
        appId = string.IsNullOrEmpty(appId) ? null : appId;
        return appId is not null;
    }

    /// <summary>Gets the command line of a launch action (empty for any other action).</summary>
    public IReadOnlyList<string> LaunchArguments =>
        Id.StartsWith(LaunchAppPrefix, StringComparison.Ordinal)
            ? Id[LaunchAppPrefix.Length..].Split(ArgumentSeparator)[1..]
            : [];
}

/// <summary>
/// Represents one notification queued for a specific user, per <c>notifications.post</c>
/// capability semantics. This record carries no rendering concerns.
/// </summary>
public sealed record Notification
{
    /// <summary>Initializes a validated notification.</summary>
    /// <param name="id">Opaque, immutable notification identifier.</param>
    /// <param name="userId">User this notification is scoped to; only that user's session observes it.</param>
    /// <param name="sourceAppId">Non-empty app ID that posted the notification.</param>
    /// <param name="severity">Visual urgency of the notification.</param>
    /// <param name="title">Non-empty short notification title.</param>
    /// <param name="message">Non-empty notification body.</param>
    /// <param name="actions">User-actionable choices offered by the notification; may be empty.</param>
    /// <param name="createdAtUtc">UTC simulation time the notification was posted.</param>
    /// <param name="expiresAtUtc">
    /// UTC simulation time after which the notification is no longer active; <see langword="null"/>
    /// means the notification never expires on its own and must be dismissed explicitly.
    /// </param>
    /// <exception cref="ArgumentException">A required field is empty or expiry precedes creation.</exception>
    public Notification(
        NotificationId id,
        LocalUserId userId,
        string sourceAppId,
        NotificationSeverity severity,
        string title,
        string message,
        IReadOnlyList<NotificationAction> actions,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sourceAppId))
        {
            throw new ArgumentException("A source app ID is required.", nameof(sourceAppId));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A title is required.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A message is required.", nameof(message));
        }

        if (expiresAtUtc is { } expiry && expiry < createdAtUtc)
        {
            throw new ArgumentException("Expiry cannot precede creation.", nameof(expiresAtUtc));
        }

        Id = id;
        UserId = userId;
        SourceAppId = sourceAppId;
        Severity = severity;
        Title = title;
        Message = message;
        Actions = [.. actions];
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Gets the opaque, immutable notification identifier.</summary>
    public NotificationId Id { get; }

    /// <summary>Gets the user this notification is scoped to.</summary>
    public LocalUserId UserId { get; }

    /// <summary>Gets the app ID that posted the notification.</summary>
    public string SourceAppId { get; }

    /// <summary>Gets the visual urgency of the notification.</summary>
    public NotificationSeverity Severity { get; }

    /// <summary>Gets the short notification title.</summary>
    public string Title { get; }

    /// <summary>Gets the notification body.</summary>
    public string Message { get; }

    /// <summary>Gets the user-actionable choices offered by the notification.</summary>
    public IReadOnlyList<NotificationAction> Actions { get; }

    /// <summary>Gets the UTC simulation time the notification was posted.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>
    /// Gets the UTC simulation time after which the notification is no longer active, or
    /// <see langword="null"/> if it never expires on its own.
    /// </summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>Determines whether the notification is still active at the given simulation time.</summary>
    /// <param name="nowUtc">Current UTC simulation time.</param>
    public bool IsActive(DateTimeOffset nowUtc) => ExpiresAtUtc is not { } expiry || nowUtc < expiry;
}

/// <summary>Queues notifications per user without any rendering concerns.</summary>
public interface INotificationQueue
{
    /// <summary>Enqueues one notification, evicting the oldest entry for that user if retention is full.</summary>
    /// <param name="notification">The notification to enqueue.</param>
    void Enqueue(Notification notification);

    /// <summary>Gets every active (non-expired), non-dismissed notification scoped to one user, oldest first.</summary>
    /// <param name="userId">User to scope the query to.</param>
    /// <param name="nowUtc">Current UTC simulation time used to filter out expired notifications.</param>
    IReadOnlyList<Notification> GetActive(LocalUserId userId, DateTimeOffset nowUtc);

    /// <summary>Dismisses one notification so it is no longer returned by <see cref="GetActive"/>.</summary>
    /// <param name="id">Identifier of the notification to dismiss.</param>
    /// <returns><see langword="true"/> if a matching, not-yet-dismissed notification was found.</returns>
    bool Dismiss(NotificationId id);
}
