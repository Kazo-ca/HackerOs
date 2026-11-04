using Kazo.Os.AppSdk.Web.Mvc;

namespace Kazo.Os.AppSdk.Web.Routing;

/// <summary>Names a route for <see cref="KazoLinkGenerator.GetPathByRouteName"/> (endpoint metadata).</summary>
/// <param name="Name">The route name.</param>
public sealed record KazoRouteNameMetadata(string Name);

/// <summary>
/// Builds URLs from the endpoints of a site (ASP.NET's <c>LinkGenerator</c>): to an action
/// (controller, action, route values) or to a named route. Unused values go to the query string.
/// </summary>
public sealed class KazoLinkGenerator
{
    private readonly KazoEndpointDataSource _endpoints;

    internal KazoLinkGenerator(KazoEndpointDataSource endpoints) => _endpoints = endpoints;

    /// <summary>Gets the path of an action, or <see langword="null"/> when no route can produce it.</summary>
    /// <param name="action">Action name.</param>
    /// <param name="controller">Controller name.</param>
    /// <param name="values">Route values (<c>id</c>...).</param>
    /// <returns>The path with its query string, starting with <c>/</c>.</returns>
    public string? GetPathByAction(string action, string controller, IReadOnlyDictionary<string, object?>? values = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(controller);
        KazoRouteValueDictionary explicitValues = values is null ? [] : new KazoRouteValueDictionary(values);
        explicitValues.Remove("controller");
        explicitValues.Remove("action");
        foreach (KazoEndpoint endpoint in _endpoints.GetRouter().Endpoints)
        {
            if (endpoint.GetMetadata<KazoControllerActionDescriptor>() is not KazoControllerActionDescriptor descriptor
                || !descriptor.ActionName.Equals(action, StringComparison.OrdinalIgnoreCase)
                || !descriptor.ControllerName.Equals(controller, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (endpoint.Pattern.TryGeneratePath(explicitValues, out string path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Gets the path of a named route, or <see langword="null"/>.</summary>
    /// <param name="routeName">The route name (<c>[HttpGet("{id}", Name = "GetNote")]</c>, <c>MapControllerRoute("blog", ...)</c>).</param>
    /// <param name="values">Route values.</param>
    public string? GetPathByRouteName(string routeName, IReadOnlyDictionary<string, object?>? values = null)
    {
        ArgumentNullException.ThrowIfNull(routeName);
        KazoRouteValueDictionary explicitValues = values is null ? [] : new KazoRouteValueDictionary(values);
        foreach (KazoEndpoint endpoint in _endpoints.GetRouter().Endpoints)
        {
            if (endpoint.GetMetadata<KazoRouteNameMetadata>() is { } name
                && name.Name.Equals(routeName, StringComparison.OrdinalIgnoreCase)
                && endpoint.Pattern.TryGeneratePath(explicitValues, out string path))
            {
                return path;
            }
        }

        return null;
    }
}

/// <summary>The endpoints of a site: filled by <c>Map*</c> while the site starts, then frozen into a router.</summary>
public sealed class KazoEndpointDataSource
{
    private readonly List<KazoEndpoint> _endpoints = [];
    private readonly Lock _gate = new();
    private KazoEndpointRouter? _router;

    /// <summary>Gets the endpoints in registration order.</summary>
    public IReadOnlyList<KazoEndpoint> Endpoints
    {
        get
        {
            lock (_gate)
            {
                return [.. _endpoints];
            }
        }
    }

    /// <summary>Adds an endpoint.</summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <exception cref="InvalidOperationException">The site already serves requests.</exception>
    public void Add(KazoEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            if (_router is not null)
            {
                throw new InvalidOperationException("Endpoints cannot be added once the site serves requests.");
            }

            _endpoints.Add(endpoint);
        }
    }

    /// <summary>Gets the router (built, and the endpoints frozen, on first use).</summary>
    public KazoEndpointRouter GetRouter()
    {
        lock (_gate)
        {
            return _router ??= new KazoEndpointRouter(_endpoints);
        }
    }
}
