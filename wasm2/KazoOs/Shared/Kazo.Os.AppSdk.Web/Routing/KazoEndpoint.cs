using Kazo.Os.AppSdk.Web.Http;

namespace Kazo.Os.AppSdk.Web.Routing;

/// <summary>A step of the pipeline, or an endpoint handler: it handles one request (ASP.NET's <c>RequestDelegate</c>).</summary>
/// <param name="context">The request.</param>
/// <returns>A task that ends when the response is ready.</returns>
public delegate Task KazoRequestDelegate(KazoHttpContext context);

/// <summary>
/// Something the router can choose for a request: a route pattern, the HTTP methods it accepts and
/// the handler that answers. Controllers' actions and minimal routes (<c>MapGet</c>...) become endpoints.
/// </summary>
public sealed class KazoEndpoint
{
    /// <summary>Initializes an endpoint.</summary>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="httpMethods">Accepted methods, upper case; empty accepts any.</param>
    /// <param name="handler">The handler.</param>
    /// <param name="displayName">A name for logs and the exception page.</param>
    /// <param name="order">Routes with a lower order are tried first (attribute routes 0, conventional routes 1 and more).</param>
    /// <param name="metadata">Extra data (the action descriptor of a controller action...).</param>
    public KazoEndpoint(
        KazoRoutePattern pattern,
        IReadOnlyList<string> httpMethods,
        KazoRequestDelegate handler,
        string displayName,
        int order = 0,
        IReadOnlyList<object>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(httpMethods);
        ArgumentNullException.ThrowIfNull(handler);
        Pattern = pattern;
        HttpMethods = [.. httpMethods.Select(method => method.ToUpperInvariant())];
        Handler = handler;
        DisplayName = displayName;
        Order = order;
        Metadata = metadata ?? [];
    }

    /// <summary>Gets the route pattern.</summary>
    public KazoRoutePattern Pattern { get; }

    /// <summary>Gets the accepted methods (empty: any).</summary>
    public IReadOnlyList<string> HttpMethods { get; }

    /// <summary>Gets the handler.</summary>
    public KazoRequestDelegate Handler { get; }

    /// <summary>Gets the display name (<c>NotesController.Get (Intranet)</c>, <c>GET /health</c>).</summary>
    public string DisplayName { get; }

    /// <summary>Gets the order.</summary>
    public int Order { get; }

    /// <summary>Gets the metadata.</summary>
    public IReadOnlyList<object> Metadata { get; }

    /// <summary>Gets the first metadata item of type <typeparamref name="T"/>, or <see langword="null"/>.</summary>
    public T? GetMetadata<T>() where T : class => Metadata.OfType<T>().FirstOrDefault();

    /// <summary>Gets whether the endpoint accepts <paramref name="method"/>.</summary>
    /// <param name="method">An upper-case method.</param>
    public bool Accepts(string method) => HttpMethods.Count == 0 || HttpMethods.Contains(method, StringComparer.Ordinal);

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}
