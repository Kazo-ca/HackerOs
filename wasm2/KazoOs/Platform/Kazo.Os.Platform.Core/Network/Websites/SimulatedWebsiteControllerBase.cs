using System.Collections.Immutable;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network.Websites;

/// <summary>
/// Shared base for all in-memory simulated website controllers.
/// Provides simple route registration (exact path or single-segment
/// parameterized paths like "/account/{id}") and request dispatch.
/// </summary>
public abstract class SimulatedWebsiteControllerBase : ISimulatedWebsiteController
{
    private readonly List<RouteEntry> _routes = [];

    /// <inheritdoc/>
    public abstract string PrimaryHostname { get; }

    /// <inheritdoc/>
    public virtual IReadOnlyCollection<string> AliasHostnames => [];

    /// <inheritdoc/>
    public abstract string Theme { get; }

    /// <inheritdoc/>
    public abstract string SiteName { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// Routes are tried in registration order. As in ASP.NET Core, a <c>HEAD</c> request is served
    /// by the matching <c>GET</c> route (the OS drops the body), <c>OPTIONS</c> on a known path
    /// answers 204 with an <c>Allow</c> header, and a known path asked with a method it does not
    /// implement answers 405 Method Not Allowed with <c>Allow</c>, not 404.
    /// </remarks>
    public SimulatedHttpResponse ProcessRequest(SimulatedHttpRequest request)
    {
        string method = request.Method.ToUpperInvariant();
        string routeMethod = method == SimulatedHttpMethods.Head ? SimulatedHttpMethods.Get : method;
        List<string> allowed = [];

        foreach (var entry in _routes)
        {
            if (!TryMatchRoute(entry.PathPattern, request.Path, out var routeParams))
                continue;

            if (!string.Equals(entry.Method, routeMethod, StringComparison.OrdinalIgnoreCase))
            {
                allowed.Add(entry.Method);
                continue;
            }

            var enriched = request with
            {
                RouteParams = routeParams
            };
            return entry.Handler(enriched);
        }

        if (allowed.Count == 0)
            return SimulatedHttpResponse.NotFound(request.Path);

        if (allowed.Contains(SimulatedHttpMethods.Get, StringComparer.OrdinalIgnoreCase))
            allowed.Add(SimulatedHttpMethods.Head);
        allowed.Add(SimulatedHttpMethods.Options);

        if (method == SimulatedHttpMethods.Options)
        {
            return SimulatedHttpResponse.NoContent() with
            {
                Headers = ImmutableDictionary<string, string>.Empty.Add("Allow", string.Join(", ", allowed.Distinct(StringComparer.OrdinalIgnoreCase)))
            };
        }

        return SimulatedHttpResponse.MethodNotAllowed(allowed);
    }

    // ── Route registration helpers ─────────────────────────────────────────

    /// <summary>Registers a handler for <paramref name="method"/> on <paramref name="pathPattern"/>.</summary>
    protected void Map(string method, string pathPattern, Func<SimulatedHttpRequest, SimulatedHttpResponse> handler) =>
        _routes.Add(new RouteEntry(method.ToUpperInvariant(), pathPattern, handler));

    /// <summary>Registers a GET handler for <paramref name="pathPattern"/>; it also serves HEAD.</summary>
    protected void Get(string pathPattern, Func<SimulatedHttpRequest, SimulatedHttpResponse> handler) =>
        Map(SimulatedHttpMethods.Get, pathPattern, handler);

    /// <summary>Registers a POST handler for <paramref name="pathPattern"/>.</summary>
    protected void Post(string pathPattern, Func<SimulatedHttpRequest, SimulatedHttpResponse> handler) =>
        Map(SimulatedHttpMethods.Post, pathPattern, handler);

    /// <summary>Registers a PUT handler for <paramref name="pathPattern"/>.</summary>
    protected void Put(string pathPattern, Func<SimulatedHttpRequest, SimulatedHttpResponse> handler) =>
        Map(SimulatedHttpMethods.Put, pathPattern, handler);

    /// <summary>Registers a PATCH handler for <paramref name="pathPattern"/>.</summary>
    protected void Patch(string pathPattern, Func<SimulatedHttpRequest, SimulatedHttpResponse> handler) =>
        Map(SimulatedHttpMethods.Patch, pathPattern, handler);

    /// <summary>Registers a DELETE handler for <paramref name="pathPattern"/>.</summary>
    protected void Delete(string pathPattern, Func<SimulatedHttpRequest, SimulatedHttpResponse> handler) =>
        Map(SimulatedHttpMethods.Delete, pathPattern, handler);

    // ── Path matching ──────────────────────────────────────────────────────

    private static bool TryMatchRoute(
        string pattern,
        string requestPath,
        out ImmutableDictionary<string, string> routeParams)
    {
        routeParams = ImmutableDictionary<string, string>.Empty;

        // Normalize trailing slashes
        var p = pattern.TrimEnd('/');
        var r = requestPath.TrimEnd('/');
        if (p.Length == 0) p = "/";
        if (r.Length == 0) r = "/";

        // Fast path: exact match
        if (string.Equals(p, r, StringComparison.OrdinalIgnoreCase))
            return true;

        // Parameterized match: /segment/{param}
        var patternParts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var requestParts = r.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (patternParts.Length != requestParts.Length)
            return false;

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < patternParts.Length; i++)
        {
            var seg = patternParts[i];
            if (seg.StartsWith('{') && seg.EndsWith('}'))
            {
                var paramName = seg[1..^1];
                builder[paramName] = Uri.UnescapeDataString(requestParts[i]);
            }
            else if (!string.Equals(seg, requestParts[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        routeParams = builder.ToImmutable();
        return true;
    }

    private sealed record RouteEntry(
        string Method,
        string PathPattern,
        Func<SimulatedHttpRequest, SimulatedHttpResponse> Handler);
}
