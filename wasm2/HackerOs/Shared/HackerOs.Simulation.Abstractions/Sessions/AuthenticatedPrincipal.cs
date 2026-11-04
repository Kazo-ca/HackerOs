using HackerOs.App.Abstractions;

namespace HackerOs.Simulation.Abstractions.Sessions;

/// <summary>
/// Represents one immutable authenticated principal created when a session enters the
/// <c>Active</c> state, per ADR 0013. Apps observe this principal but can never mutate or
/// elevate it; elevation is a separate, explicit, audited, operation-scoped mechanism.
/// </summary>
public sealed record AuthenticatedPrincipal
{
    /// <summary>Initializes a validated authenticated principal.</summary>
    /// <param name="sessionId">Identifier of the session this principal belongs to.</param>
    /// <param name="userId">Opaque identifier of the authenticated local user.</param>
    /// <param name="loginName">Normalized login name of the authenticated local user.</param>
    /// <param name="displayName">Display name captured at authentication time.</param>
    /// <param name="authority">Authority granted to the session; never <see cref="AppAuthority.System"/>.</param>
    /// <param name="primaryGroupId">The user's primary group at authentication time.</param>
    /// <param name="groupIds">All group memberships at authentication time, including the primary group.</param>
    /// <param name="installationId">Identifier of the KazoOS installation/profile hosting the session.</param>
    /// <param name="deviceId">Identifier of the physical/browser device hosting the session.</param>
    /// <param name="authenticatedAtUtc">UTC simulation time authentication completed.</param>
    /// <param name="homePath">
    /// Home directory when it is not <c>/home/&lt;login&gt;</c>: a service account's, such as
    /// <c>/var/lib/hackeros-svc</c> (ADR 0097, section 4). <see langword="null"/> for a login account.
    /// </param>
    /// <exception cref="ArgumentException">An invariant is violated.</exception>
    public AuthenticatedPrincipal(
        SessionId sessionId,
        LocalUserId userId,
        LocalLoginName loginName,
        string displayName,
        AppAuthority authority,
        LocalGroupId primaryGroupId,
        IReadOnlyCollection<LocalGroupId> groupIds,
        InstallationId installationId,
        DeviceId deviceId,
        DateTimeOffset authenticatedAtUtc,
        string? homePath = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A display name is required.", nameof(displayName));
        }

        if (authority == AppAuthority.System)
        {
            throw new ArgumentException(
                "A session principal can never carry System authority.", nameof(authority));
        }

        if (!groupIds.Contains(primaryGroupId))
        {
            throw new ArgumentException(
                "Group memberships must include the primary group.", nameof(groupIds));
        }

        SessionId = sessionId;
        UserId = userId;
        LoginName = loginName;
        DisplayName = displayName;
        Authority = authority;
        PrimaryGroupId = primaryGroupId;
        GroupIds = [.. groupIds];
        InstallationId = installationId;
        DeviceId = deviceId;
        AuthenticatedAtUtc = authenticatedAtUtc;
        _homePath = homePath;
    }

    private readonly string? _homePath;

    /// <summary>Gets the identifier of the session this principal belongs to.</summary>
    public SessionId SessionId { get; }

    /// <summary>Gets the opaque identifier of the authenticated local user.</summary>
    public LocalUserId UserId { get; }

    /// <summary>Gets the normalized login name of the authenticated local user.</summary>
    public LocalLoginName LoginName { get; }

    /// <summary>Gets the display name captured at authentication time.</summary>
    public string DisplayName { get; }

    /// <summary>Gets the authority granted to the session; never <see cref="AppAuthority.System"/>.</summary>
    public AppAuthority Authority { get; }

    /// <summary>Gets the user's primary group at authentication time.</summary>
    public LocalGroupId PrimaryGroupId { get; }

    /// <summary>Gets all group memberships at authentication time, including the primary group.</summary>
    public IReadOnlyCollection<LocalGroupId> GroupIds { get; }

    /// <summary>Gets the identifier of the KazoOS installation/profile hosting the session.</summary>
    public InstallationId InstallationId { get; }

    /// <summary>Gets the identifier of the physical/browser device hosting the session.</summary>
    public DeviceId DeviceId { get; }

    /// <summary>Gets the UTC simulation time authentication completed.</summary>
    public DateTimeOffset AuthenticatedAtUtc { get; }

    /// <summary>Gets the authenticated user's home directory path.</summary>
    public string HomePath => _homePath ?? $"/home/{LoginName.Value}";

    /// <summary>Determines whether the principal carries at least the required authority.</summary>
    /// <param name="required">Minimum authority required by the operation.</param>
    public bool HasAuthority(AppAuthority required) => AppAuthorityPolicy.Satisfies(Authority, required);
}
