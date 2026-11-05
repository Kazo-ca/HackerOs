using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Sessions;

/// <summary>
/// <c>kazoos-svc</c>, the account the system services run as (ADR 0097, section 4): like the
/// accounts a Linux package creates for its daemon (<c>systemd-resolve</c>, <c>www-data</c>), it owns
/// a home under <c>/var/lib</c>, has no password and no shell (<c>/usr/sbin/nologin</c>), and never
/// appears on the login screen. It is built into the OS: it is not stored in the user repository,
/// so no migration creates it, and its identifiers are the same on every installation.
/// </summary>
public static class ServiceAccount
{
    /// <summary>The login name of the service account.</summary>
    public const string LoginName = "kazoos-svc";

    /// <summary>The service account's home directory.</summary>
    public const string HomePath = "/var/lib/kazoos-svc";

    /// <summary>The shell of the service account: logging in is refused.</summary>
    public const string Shell = "/usr/sbin/nologin";

    /// <summary>Gets the fixed user ID of the service account.</summary>
    public static LocalUserId UserId { get; } = LocalUserId.FromGuid(new Guid("00000000-0000-4000-8000-0000000003e7"));

    /// <summary>Gets the fixed primary group of the service account (its own group, like <c>useradd --system</c>).</summary>
    public static LocalGroupId GroupId { get; } = LocalGroupId.FromGuid(new Guid("00000000-0000-4000-8000-0000000103e7"));

    /// <summary>
    /// Gets the fixed session the system services belong to: there is no login behind it, as the
    /// processes systemd starts before any login belong to no user session.
    /// </summary>
    public static SessionId SystemSessionId { get; } = SessionId.FromGuid(new Guid("00000000-0000-4000-8000-000000000001"));

    /// <summary>Gets whether a login name is the service account's, which no login account may take.</summary>
    /// <param name="loginName">A login name.</param>
    public static bool IsReserved(string loginName) =>
        string.Equals(loginName, LoginName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the principal the system services run as.</summary>
    /// <param name="installationId">This installation.</param>
    /// <param name="deviceId">This device.</param>
    /// <param name="startedAtUtc">When the system services started (the boot).</param>
    public static AuthenticatedPrincipal CreatePrincipal(InstallationId installationId, DeviceId deviceId, DateTimeOffset startedAtUtc) => new(
        SystemSessionId,
        UserId,
        LocalLoginName.Parse(LoginName),
        "Kazo OS system services",
        AppAuthority.User,
        GroupId,
        [GroupId],
        installationId,
        deviceId,
        startedAtUtc,
        HomePath);
}
