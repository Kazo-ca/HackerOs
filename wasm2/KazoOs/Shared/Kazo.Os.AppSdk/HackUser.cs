using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Describes the user a process runs as (ADR 0056, section 1), like the <c>passwd</c> entry Linux
/// returns for <c>getpwuid(geteuid())</c>.
/// </summary>
/// <param name="LoginName">Login name (<c>benoit</c>, or <c>root</c> under <c>sudo</c>).</param>
/// <param name="DisplayName">Display name captured at login (the GECOS field); <c>root</c> under <c>sudo</c>.</param>
/// <param name="HomeDirectory">Canonical home directory (<c>/home/benoit</c>, or <c>/root</c> under <c>sudo</c>).</param>
/// <param name="IsAdministrator">Whether the user is an administrator, or the process runs as root.</param>
public sealed record HackUserInfo(string LoginName, string DisplayName, string HomeDirectory, bool IsAdministrator);

/// <summary>
/// The user the current process runs as (ADR 0056, section 1). Replaces
/// <c>ISessionService.CurrentPrincipal</c>, which apps may no longer inject (ADR 0055).
/// </summary>
/// <remarks>
/// No capability is needed: on Linux every process knows its own user (<c>getuid</c>,
/// <c>getpwuid</c>). The value describes the <b>effective</b> user: a process started through
/// <c>sudo</c> (ADR 0042) sees <c>root</c> and <c>/root</c>, like <c>whoami</c> under <c>sudo</c>.
/// </remarks>
public static class HackUser
{
    /// <summary>Login name, display name and home directory of root, the effective user under <c>sudo</c>.</summary>
    public static readonly HackUserInfo Root = new("root", "root", "/root", IsAdministrator: true);

    /// <summary>Gets the effective user of the current process.</summary>
    /// <exception cref="HackExecutionContextMissingException">No app context is active.</exception>
    public static HackUserInfo Current => Describe(HackExecutionContext.Current);

    /// <summary>
    /// Describes the effective user of <paramref name="context"/>. Useful where no ambient scope is
    /// active, for instance when a component caches the value for rendering.
    /// </summary>
    /// <param name="context">The app context whose user is described.</param>
    public static HackUserInfo Describe(IAppExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IsRootElevated)
        {
            return Root;
        }

        return new HackUserInfo(
            context.UserLoginName,
            context.UserDisplayName,
            context.HomeDirectory,
            AppAuthorityPolicy.Satisfies(context.UserAuthority, AppAuthority.Administrator));
    }
}
