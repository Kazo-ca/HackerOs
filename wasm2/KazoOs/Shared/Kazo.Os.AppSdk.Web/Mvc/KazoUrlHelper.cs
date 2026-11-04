using System.Diagnostics.CodeAnalysis;
using Kazo.Os.AppSdk.Web.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.AppSdk.Web.Mvc;

/// <summary>Builds URLs inside a request (ASP.NET's <c>IUrlHelper</c>); views (web/7) use it too.</summary>
public interface IKazoUrlHelper
{
    /// <summary>Gets the path of an action; the current action or controller when <see langword="null"/>.</summary>
    /// <param name="action">Action name.</param>
    /// <param name="controller">Controller name.</param>
    /// <returns>The path, or <see langword="null"/> when no route produces it.</returns>
    string? Action(string? action = null, string? controller = null);

    /// <summary>Gets the path of an action with route values.</summary>
    /// <typeparam name="TValues">Type of the route values (inferred).</typeparam>
    /// <param name="action">Action name.</param>
    /// <param name="controller">Controller name.</param>
    /// <param name="values">Route values: a dictionary or an anonymous object.</param>
    /// <returns>The path, or <see langword="null"/> when no route produces it.</returns>
    string? Action<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(string? action, string? controller, TValues values);

    /// <summary>Gets the path of a named route.</summary>
    /// <param name="routeName">Route name.</param>
    string? RouteUrl(string routeName);

    /// <summary>Gets the path of a named route with route values.</summary>
    /// <typeparam name="TValues">Type of the route values (inferred).</typeparam>
    /// <param name="routeName">Route name.</param>
    /// <param name="values">Route values.</param>
    string? RouteUrl<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(string routeName, TValues values);

    /// <summary>Turns <c>~/css/site.css</c> into <c>/css/site.css</c>; other paths are returned as is.</summary>
    /// <param name="contentPath">A path.</param>
    string Content(string contentPath);

    /// <summary>Gets whether <paramref name="url"/> stays on this site (see <see cref="KazoUrlHelper.IsLocalUrl"/>).</summary>
    /// <param name="url">A URL.</param>
    bool IsLocalUrl([NotNullWhen(true)] string? url);
}

/// <summary>The <see cref="IKazoUrlHelper"/> of a request, over the site's <see cref="KazoLinkGenerator"/>.</summary>
public sealed class KazoUrlHelper : IKazoUrlHelper
{
    private readonly KazoActionContext _context;

    /// <summary>Initializes a helper for a request.</summary>
    /// <param name="context">The action context.</param>
    public KazoUrlHelper(KazoActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public string? Action(string? action = null, string? controller = null) => Action(action, controller, new KazoRouteValueDictionary());

    /// <inheritdoc />
    public string? Action<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(string? action, string? controller, TValues values)
    {
        string? actionName = action ?? _context.ActionDescriptor?.ActionName ?? _context.RouteValues.GetString("action");
        string? controllerName = controller ?? _context.ActionDescriptor?.ControllerName ?? _context.RouteValues.GetString("controller");
        if (actionName is null || controllerName is null)
        {
            return null;
        }

        return Links.GetPathByAction(actionName, controllerName, KazoRouteValueDictionary.FromObject(values));
    }

    /// <inheritdoc />
    public string? RouteUrl(string routeName) => Links.GetPathByRouteName(routeName);

    /// <inheritdoc />
    public string? RouteUrl<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TValues>(string routeName, TValues values) =>
        Links.GetPathByRouteName(routeName, KazoRouteValueDictionary.FromObject(values));

    /// <inheritdoc />
    public string Content(string contentPath)
    {
        ArgumentNullException.ThrowIfNull(contentPath);
        return contentPath.StartsWith("~/", StringComparison.Ordinal) ? contentPath[1..] : contentPath;
    }

    /// <inheritdoc />
    bool IKazoUrlHelper.IsLocalUrl([NotNullWhen(true)] string? url) => IsLocalUrl(url);

    /// <summary>
    /// Gets whether <paramref name="url"/> is a path of this site: it starts with <c>/</c> (not <c>//</c>
    /// nor <c>/\</c>, which browsers treat as another host) or with <c>~/</c>. Same rule as ASP.NET.
    /// </summary>
    /// <param name="url">A URL.</param>
    public static bool IsLocalUrl([NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url[0] == '/')
        {
            return url.Length == 1 || (url[1] != '/' && url[1] != '\\');
        }

        return url.Length > 1 && url[0] == '~' && url[1] == '/' && (url.Length == 2 || (url[2] != '/' && url[2] != '\\'));
    }

    private KazoLinkGenerator Links => _context.HttpContext.RequestServices.GetRequiredService<KazoLinkGenerator>();
}
