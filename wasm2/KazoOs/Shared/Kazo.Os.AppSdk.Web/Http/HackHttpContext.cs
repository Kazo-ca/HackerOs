using System.Globalization;
using System.Net;
using System.Threading;
using Kazo.Os.AppSdk.Web.Routing;
using Kazo.Os.AppSdk.Web.Sessions;

namespace Kazo.Os.AppSdk.Web.Http;

/// <summary>
/// Everything about one HTTP request while it crosses the pipeline, modeled on ASP.NET Core's
/// <c>HttpContext</c> (ADR 0076 §2): <see cref="Request"/>, <see cref="Response"/>, <see cref="Items"/>,
/// <see cref="RequestServices"/> (the request's DI scope), <see cref="Session"/> and <see cref="RequestAborted"/>.
/// </summary>
public sealed class HackHttpContext
{
    private static long s_requestCounter;
    private HackSession? _session;

    internal HackHttpContext(HackWebRequestData data, IServiceProvider requestServices, string connectionId, CancellationToken requestAborted)
    {
        Request = new HackHttpRequest(data);
        RequestServices = requestServices;
        RequestAborted = requestAborted;
        IPEndPoint local = data.LocalEndPoint ?? new IPEndPoint(IPAddress.Loopback, data.Url.Port);
        Connection = new HackConnectionInfo(connectionId, data.RemoteEndPoint, local);
        long number = Interlocked.Increment(ref s_requestCounter);
        // Kestrel's shape: "<connection id>:<request number in hex, 8 digits>".
        TraceIdentifier = string.Create(CultureInfo.InvariantCulture, $"{connectionId}:{number:X8}");
    }

    /// <summary>Gets the request.</summary>
    public HackHttpRequest Request { get; }

    /// <summary>Gets the response being built.</summary>
    public HackHttpResponse Response { get; } = new();

    /// <summary>Gets the connection: client and local end points.</summary>
    public HackConnectionInfo Connection { get; }

    /// <summary>Gets a bag shared by the middlewares and the endpoint of this request only.</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    /// <summary>Gets the services of this request's DI scope (scoped services live until the response is sent).</summary>
    public IServiceProvider RequestServices { get; }

    /// <summary>Gets a token cancelled when the site stops while the request runs.</summary>
    public CancellationToken RequestAborted { get; }

    /// <summary>Gets an identifier for this request in logs and problem details (<c>traceId</c>).</summary>
    public string TraceIdentifier { get; }

    /// <summary>Gets the endpoint chosen by the router, or <see langword="null"/> when none matched.</summary>
    public HackEndpoint? Endpoint { get; internal set; }

    /// <summary>Gets whether a session middleware (<c>UseCookieSession</c>) runs for this request.</summary>
    public bool HasSession => _session is not null;

    /// <summary>Gets the user's session.</summary>
    /// <exception cref="InvalidOperationException">No session middleware is configured (same message as ASP.NET).</exception>
    public HackSession Session
    {
        get => _session ?? throw new InvalidOperationException(
            "Session has not been configured for this application or request. Call app.UseCookieSession() before the endpoints.");
        internal set => _session = value;
    }
}

/// <summary>The connection a request arrived on.</summary>
/// <param name="Id">Connection identifier (also the start of <see cref="HackHttpContext.TraceIdentifier"/>).</param>
/// <param name="RemoteEndPoint">The client's address and port.</param>
/// <param name="LocalEndPoint">The address and port the site listens on.</param>
public sealed record HackConnectionInfo(string Id, IPEndPoint RemoteEndPoint, IPEndPoint LocalEndPoint);
