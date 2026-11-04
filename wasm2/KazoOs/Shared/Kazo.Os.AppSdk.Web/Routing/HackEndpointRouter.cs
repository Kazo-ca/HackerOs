namespace Kazo.Os.AppSdk.Web.Routing;

/// <summary>What the router found for a request.</summary>
public enum HackRouteMatchKind
{
    /// <summary>No route matches the path: <c>404 Not Found</c>.</summary>
    NotFound,

    /// <summary>An endpoint matches the path and the method.</summary>
    Matched,

    /// <summary>Routes match the path but none accepts the method: <c>405 Method Not Allowed</c> with <c>Allow</c>.</summary>
    MethodNotAllowed,

    /// <summary>An <c>OPTIONS</c> request no endpoint handles: <c>204</c> with <c>Allow</c>.</summary>
    Options
}

/// <summary>The result of <see cref="HackEndpointRouter.Match"/>.</summary>
/// <param name="Kind">What was found.</param>
/// <param name="Endpoint">The endpoint when <see cref="HackRouteMatchKind.Matched"/>.</param>
/// <param name="Values">Its route values.</param>
/// <param name="AllowedMethods">Methods accepted for the path (for <c>Allow</c>).</param>
public sealed record HackRouteMatch(
    HackRouteMatchKind Kind,
    HackEndpoint? Endpoint,
    HackRouteValueDictionary Values,
    IReadOnlyList<string> AllowedMethods);

/// <summary>Thrown when two endpoints of the same order and precedence both accept a request (ASP.NET's <c>AmbiguousMatchException</c>).</summary>
public sealed class HackAmbiguousRouteException(string message) : Exception(message);

/// <summary>
/// Chooses the endpoint of a request, as ASP.NET Core's endpoint routing does (ADR 0076 §3): lowest
/// <see cref="HackEndpoint.Order"/> first, then the most specific template (literal before
/// constrained parameter before parameter before optional before catch-all), then the first registered.
/// <c>HEAD</c> uses a <c>GET</c> endpoint; a method no endpoint accepts gives <c>405</c>.
/// </summary>
public sealed class HackEndpointRouter
{
    private readonly HackEndpoint[] _endpoints;

    /// <summary>Initializes a router over <paramref name="endpoints"/>.</summary>
    /// <param name="endpoints">The endpoints, in registration order.</param>
    public HackEndpointRouter(IEnumerable<HackEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _endpoints = [.. endpoints
            .Select((endpoint, index) => (endpoint, index))
            .OrderBy(item => item.endpoint.Order)
            .ThenBy(item => item.endpoint.Pattern.Precedence, PrecedenceComparer.Instance)
            // Like ASP.NET's HTTP method policy: an endpoint restricted to methods beats one accepting any.
            .ThenBy(item => item.endpoint.HttpMethods.Count == 0 ? 1 : 0)
            .ThenBy(item => item.index)
            .Select(item => item.endpoint)];
    }

    /// <summary>Gets the endpoints in the order they are tried.</summary>
    public IReadOnlyList<HackEndpoint> Endpoints => _endpoints;

    /// <summary>Finds the endpoint for <paramref name="method"/> and <paramref name="path"/>.</summary>
    /// <param name="method">Upper-case HTTP method.</param>
    /// <param name="path">Decoded request path.</param>
    /// <returns>The match.</returns>
    /// <exception cref="HackAmbiguousRouteException">Two equally specific endpoints accept the request.</exception>
    public HackRouteMatch Match(string method, string path)
    {
        string[] segments = HackRoutePattern.SplitPath(path);
        SortedSet<string> allowed = new(StringComparer.Ordinal);
        HackEndpoint? chosen = null;
        HackRouteValueDictionary chosenValues = [];
        bool anyPathMatch = false;
        foreach (HackEndpoint endpoint in _endpoints)
        {
            if (!endpoint.Pattern.TryMatch(segments, out HackRouteValueDictionary values))
            {
                continue;
            }

            anyPathMatch = true;
            bool accepts = endpoint.Accepts(method) || (method == "HEAD" && endpoint.Accepts("GET"));
            if (chosen is not null)
            {
                if (accepts && endpoint.Order == chosen.Order
                    && PrecedenceComparer.Instance.Compare(endpoint.Pattern.Precedence, chosen.Pattern.Precedence) == 0
                    && endpoint.HttpMethods.Count == chosen.HttpMethods.Count)
                {
                    throw new HackAmbiguousRouteException(
                        $"The request matched multiple endpoints: {chosen.DisplayName}, {endpoint.DisplayName}.");
                }

                continue;
            }

            if (accepts)
            {
                chosen = endpoint;
                chosenValues = values;
                continue;
            }

            foreach (string accepted in endpoint.HttpMethods)
            {
                allowed.Add(accepted);
                if (accepted == "GET")
                {
                    allowed.Add("HEAD");
                }
            }
        }

        if (chosen is not null)
        {
            return new HackRouteMatch(HackRouteMatchKind.Matched, chosen, chosenValues, []);
        }

        if (!anyPathMatch)
        {
            return new HackRouteMatch(HackRouteMatchKind.NotFound, null, [], []);
        }

        allowed.Add("OPTIONS");
        return new HackRouteMatch(
            method == "OPTIONS" ? HackRouteMatchKind.Options : HackRouteMatchKind.MethodNotAllowed,
            null,
            [],
            [.. allowed]);
    }

    /// <summary>Compares precedences segment by segment; a shorter template wins over a longer one with the same start.</summary>
    private sealed class PrecedenceComparer : IComparer<IReadOnlyList<int>>
    {
        public static readonly PrecedenceComparer Instance = new();

        public int Compare(IReadOnlyList<int>? x, IReadOnlyList<int>? y)
        {
            x ??= [];
            y ??= [];
            for (int index = 0; index < Math.Min(x.Count, y.Count); index++)
            {
                int compared = x[index].CompareTo(y[index]);
                if (compared != 0)
                {
                    return compared;
                }
            }

            return x.Count.CompareTo(y.Count);
        }
    }
}
