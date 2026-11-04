using Kazo.Os.AppSdk.Web.Http;

namespace Kazo.Os.Samples.Intranet.Services;

/// <summary>
/// What the site keeps in the cookie session (<c>UseCookieSession</c>, ADR 0076): the visitor's display
/// name. The session lives in the memory of the site process and ends when the site stops.
/// </summary>
public static class IntranetSession
{
    /// <summary>The session key of the display name.</summary>
    public const string DisplayNameKey = "displayName";

    /// <summary>The author written on notes when nobody chose a name.</summary>
    public const string Anonymous = "anonymous";

    /// <summary>Returns the display name of the request's session, or <see langword="null"/> when none was chosen.</summary>
    /// <param name="context">The request.</param>
    public static string? DisplayName(HackHttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.HasSession ? context.Session.GetString(DisplayNameKey) : null;
    }
}
