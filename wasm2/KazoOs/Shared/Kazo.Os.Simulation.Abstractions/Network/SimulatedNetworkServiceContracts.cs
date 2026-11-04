namespace Kazo.Os.Simulation.Abstractions.Network;

// ---------------------------------------------------------------------------
// Website controller interface + registry contracts.
// ---------------------------------------------------------------------------

/// <summary>
/// Route pattern for a simulated website.
/// Supports exact paths ("/" or "/account") and single-segment parameterized
/// paths ("/account/{id}"). The extracted parameter values are added to
/// <see cref="SimulatedHttpRequest.RouteParams"/> before dispatch.
/// </summary>
public sealed record SimulatedRoute(string Method, string PathPattern);

/// <summary>
/// Contract for a simulated website controller.
/// Implementations register against one or more hostnames and handle
/// <see cref="SimulatedHttpRequest"/> values, returning structured
/// <see cref="SimulatedHttpResponse"/> values.
/// No I/O, real HTTP, or DOM interaction is permitted in implementations.
/// </summary>
public interface ISimulatedWebsiteController
{
    /// <summary>
    /// Canonical primary hostname (e.g. "hackersearch.net").
    /// </summary>
    string PrimaryHostname { get; }

    /// <summary>
    /// Additional hostnames this controller handles (e.g. "www.hackersearch.net").
    /// May be empty.
    /// </summary>
    IReadOnlyCollection<string> AliasHostnames { get; }

    /// <summary>
    /// The page theme token (e.g. "hacker", "bank", "darknet") used by the
    /// Blazor renderer to select CSS custom-property overrides.
    /// </summary>
    string Theme { get; }

    /// <summary>
    /// Human-readable site name shown in browser bookmarks/tabs.
    /// </summary>
    string SiteName { get; }

    /// <summary>
    /// Process a simulated request and return a structured response.
    /// Must never block the thread, call real network APIs, or modify
    /// shared mutable state concurrently.
    /// </summary>
    SimulatedHttpResponse ProcessRequest(SimulatedHttpRequest request);
}

/// <summary>
/// Registry of all simulated website controllers known to the platform.
/// Populated at boot from the host composition root.
/// </summary>
public interface ISimulatedWebsiteRegistry
{
    /// <summary>
    /// Attempts to find a controller that handles <paramref name="hostname"/>.
    /// Returns <c>null</c> when the hostname is not registered.
    /// </summary>
    ISimulatedWebsiteController? FindController(string hostname);

    /// <summary>
    /// Returns all registered controllers in insertion order.
    /// </summary>
    IReadOnlyList<ISimulatedWebsiteController> All { get; }
}

// ---------------------------------------------------------------------------
// Simulated DNS and network service contracts.
// ---------------------------------------------------------------------------

/// <summary>
/// Provides hostname-to-IP and reverse-IP-to-hostname resolution for the
/// simulated network. Never calls real DNS; uses an in-memory registry seeded
/// from the known <see cref="SimulatedHost"/> catalog.
/// </summary>
public interface ISimulatedDns
{
    /// <summary>
    /// Resolves a hostname to its simulated IPv4 address.
    /// Returns <c>null</c> when the hostname is not in the simulated zone.
    /// </summary>
    string? Resolve(string hostname);

    /// <summary>
    /// Reverse-resolves an IPv4 address to its canonical hostname.
    /// Returns <c>null</c> when no host matches.
    /// </summary>
    string? ReverseLookup(string ip);

    /// <summary>Returns all forward DNS records in the simulated zone.</summary>
    IReadOnlyList<(string Hostname, string Ip)> AllRecords { get; }
}

/// <summary>
/// Outcome of a simulated HTTP navigation through the network service.
/// Distinct from <see cref="SimulatedHttpResponse"/> because the service
/// follows redirects and applies cookie state before returning to callers.
/// </summary>
/// <param name="FinalUrl">Final URL after all redirects.</param>
/// <param name="StatusCode">Final HTTP status code.</param>
/// <param name="Page">Structured page content (null for non-content status codes).</param>
/// <param name="NetworkError">Error description when the host is down or the DNS name is unknown.</param>
/// <param name="RedirectCount">Number of redirects followed (max 10).</param>
/// <param name="Body">Body of the final response (ADR 0074, section 1), or null.</param>
public sealed record SimulatedNavigationResult(
    string FinalUrl,
    int StatusCode,
    SimulatedPage? Page,
    string? NetworkError,
    int RedirectCount,
    SimulatedHttpBody? Body = null)
{
    /// <summary>True when navigation completed with a renderable page.</summary>
    public bool IsSuccess => NetworkError is null && Page is not null;
}

/// <summary>
/// Outcome of one HTTP exchange through <see cref="ISimulatedNetworkService.Send"/>: the raw
/// response (redirects are not followed), or the network error that prevented any answer.
/// </summary>
/// <param name="Response">The site's response; <see langword="null"/> when <paramref name="NetworkError"/> is set.</param>
/// <param name="NetworkError">One of <see cref="SimulatedNetworkErrors"/>, or <see langword="null"/>.</param>
public sealed record SimulatedExchangeResult(SimulatedHttpResponse? Response, string? NetworkError)
{
    /// <summary>Gets whether a response came back.</summary>
    public bool Succeeded => NetworkError is null && Response is not null;
}

/// <summary>
/// Stable error codes returned by the simulated network service.
/// </summary>
public static class SimulatedNetworkErrors
{
    /// <summary>DNS has no record for the host name.</summary>
    public const string UnknownHost      = "ERR_NAME_NOT_RESOLVED";

    /// <summary>The host exists but is down: it does not answer at all.</summary>
    public const string HostDown         = "ERR_HOST_UNREACHABLE";

    /// <summary>The host is up but nothing listens on the port (a TCP reset).</summary>
    public const string ConnectionRefused = "ERR_CONNECTION_REFUSED";

    /// <summary>More than the allowed number of redirects (10) were chained.</summary>
    public const string TooManyRedirects = "ERR_TOO_MANY_REDIRECTS";

    /// <summary>The network has no route to the destination address.</summary>
    public const string NoRoute          = "ERR_NO_ROUTE";
}

/// <summary>
/// High-level simulated network service.
/// Orchestrates DNS resolution, host reachability checks, cookie management,
/// redirect following, and controller dispatch. All operations are
/// synchronous and never touch real network APIs.
/// </summary>
public interface ISimulatedNetworkService
{
    /// <summary>
    /// Attempts to reach the host at the given URL as an HTTP navigation.
    /// Follows redirects up to 10 hops, applying and collecting cookies from
    /// the supplied <paramref name="sessionCookies"/> jar.
    /// </summary>
    /// <param name="url">The URL to navigate to (must start with http:// or https://).</param>
    /// <param name="sessionCookies">
    /// Current per-host cookie jar; updated in place with Set-Cookie directives
    /// from each response. Pass an empty mutable dictionary for a new session.
    /// </param>
    SimulatedNavigationResult Navigate(
        string url,
        Dictionary<string, Dictionary<string, string>> sessionCookies);

    /// <summary>
    /// Sends a simulated HTTP POST and returns the raw response.
    /// Redirects are NOT automatically followed; the caller must call
    /// <see cref="Navigate"/> if needed.
    /// </summary>
    SimulatedHttpResponse Post(
        string url,
        System.Collections.Immutable.ImmutableDictionary<string, string> formBody,
        Dictionary<string, Dictionary<string, string>> sessionCookies);

    /// <summary>
    /// Sends one HTTP request with any method, headers and body (ADR 0074, section 1) and returns
    /// the raw response: redirects are NOT followed, so the caller decides how a 303 or a 307
    /// continues. Cookies of the target host are taken from, and Set-Cookie applied to,
    /// <paramref name="sessionCookies"/>.
    /// </summary>
    /// <param name="method">HTTP method (any case).</param>
    /// <param name="url">Absolute http(s) URL; a bare host gets <c>https://</c>.</param>
    /// <param name="headers">Request headers; <c>Content-Type</c> and <c>Content-Length</c> come from <paramref name="body"/>.</param>
    /// <param name="body">Request body, or <see langword="null"/>.</param>
    /// <param name="sessionCookies">Per-host cookie jar, updated in place.</param>
    SimulatedExchangeResult Send(
        string method,
        string url,
        IEnumerable<KeyValuePair<string, string>>? headers,
        SimulatedHttpBody? body,
        Dictionary<string, Dictionary<string, string>> sessionCookies);

    /// <summary>
    /// Performs a simulated ping to the hostname or IP.
    /// Returns the synthetic latency in milliseconds, or null when the host
    /// is down or the address is unknown.
    /// </summary>
    double? Ping(string hostnameOrIp);

    /// <summary>
    /// Scans a host's port range and returns the visible ports.
    /// Returns an empty enumerable when the host is unknown or unreachable.
    /// </summary>
    IReadOnlyList<SimulatedPort> ScanPorts(string hostnameOrIp, int firstPort, int lastPort);

    /// <summary>
    /// Looks up host metadata for the given hostname or IP address.
    /// Returns null when the address is unknown.
    /// </summary>
    SimulatedHost? GetHost(string hostnameOrIp);

    /// <summary>
    /// Returns all hosts registered in the simulated network.
    /// </summary>
    IReadOnlyList<SimulatedHost> AllHosts { get; }

    /// <summary>
    /// The DNS service for direct hostname resolution.
    /// </summary>
    ISimulatedDns Dns { get; }
}
