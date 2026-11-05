using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Kazo.Os.AppSdk.Web.Diagnostics;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.AppSdk.Web.Routing;
using Kazo.Os.AppSdk.Web.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.AppSdk.Web.Hosting;

/// <summary>A middleware class (ASP.NET's <c>IMiddleware</c>): runs code around the rest of the pipeline.</summary>
public interface IKazoMiddleware
{
    /// <summary>Handles the request; call <paramref name="next"/> to continue the pipeline.</summary>
    /// <param name="context">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    Task InvokeAsync(KazoHttpContext context, KazoRequestDelegate next);
}

/// <summary>
/// A site: its services, its middleware pipeline and its endpoints (ASP.NET Core's <c>WebApplication</c>,
/// ADR 0076 §2). Middlewares run in the order they are added, around the endpoint the router chose;
/// the router itself runs first, so a middleware sees <see cref="KazoHttpContext.Endpoint"/> and the
/// route values.
/// </summary>
/// <remarks>
/// <see cref="ProcessAsync"/> serves one request; <see cref="KazoWebHost"/> calls it for each request of
/// its <c>KazoHttpListener</c>, and tests call it directly. The pipeline and the endpoints are frozen at the
/// first request.
/// </remarks>
public sealed class KazoWebApplication : IAsyncDisposable
{
    private static long s_connections;
    private readonly KazoWebApplicationBuilder _builder;
    private readonly ServiceProvider _services;
    private readonly KazoEndpointDataSource _endpoints;
    private readonly List<Func<KazoRequestDelegate, KazoRequestDelegate>> _middlewares = [];
    private readonly Lock _gate = new();
    private KazoRequestDelegate? _pipeline;
    private bool _exceptionPage;
    private bool _controllersMapped;
    private bool _conventionalRouteDeclared;
    private int _conventionalOrder = 1;

    internal KazoWebApplication(KazoWebApplicationBuilder builder, ServiceProvider services, KazoEndpointDataSource endpoints)
    {
        _builder = builder;
        _services = services;
        _endpoints = endpoints;
    }

    /// <summary>Gets the site's root services (singletons; use <see cref="KazoHttpContext.RequestServices"/> in a request).</summary>
    public IServiceProvider Services => _services;

    /// <summary>Gets the app id.</summary>
    public string ApplicationId => _builder.ApplicationId;

    /// <summary>Gets the URLs the site serves.</summary>
    public IReadOnlyList<string> Urls => _builder.Urls;

    /// <summary>Gets the endpoints registered so far, in registration order.</summary>
    public IReadOnlyList<KazoEndpoint> Endpoints => _endpoints.Endpoints;

    /// <summary>Raised when a request fails with an exception (the host writes it to the site's log).</summary>
    public event Action<KazoHttpContext, Exception>? UnhandledException;

    /// <summary>Adds a middleware that wraps the rest of the pipeline.</summary>
    /// <param name="middleware">Receives the next step, returns the step to run instead.</param>
    public KazoWebApplication Use(Func<KazoRequestDelegate, KazoRequestDelegate> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        lock (_gate)
        {
            EnsureNotStarted();
            _middlewares.Add(middleware);
        }

        return this;
    }

    /// <summary>Adds an inline middleware: <c>app.Use(async (context, next) =&gt; { ...; await next(); })</c>.</summary>
    /// <param name="middleware">The middleware.</param>
    public KazoWebApplication Use(Func<KazoHttpContext, Func<Task>, Task> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return Use(next => context => middleware(context, () => next(context)));
    }

    /// <summary>Adds a middleware class, taken from the services or created with them (once, like ASP.NET's convention middleware).</summary>
    /// <typeparam name="T">The middleware class.</typeparam>
    public KazoWebApplication UseMiddleware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : IKazoMiddleware
    {
        T instance = ActivatorUtilities.GetServiceOrCreateInstance<T>(_services);
        return Use(next => context => instance.InvokeAsync(context, next));
    }

    /// <summary>
    /// Shows a detailed error page (exception, stack, request) instead of a bare <c>500</c>, like
    /// <c>UseDeveloperExceptionPage</c>. It catches failures of the whole pipeline, wherever it is called.
    /// </summary>
    public KazoWebApplication UseExceptionPage()
    {
        lock (_gate)
        {
            EnsureNotStarted();
            _exceptionPage = true;
        }

        return this;
    }

    /// <summary>
    /// Serves the project's <c>wwwroot/</c>, embedded in the site's assembly at build, read-only
    /// (ADR 0073 §2, ADR 0083 §6): <c>/css/site.css</c>, <c>/</c> → <c>index.html</c>. Put it before
    /// <see cref="MapControllers"/>'s middlewares so a file answers without reaching an action.
    /// </summary>
    /// <param name="configure">Changes the options (assembly, request path, default files, caching).</param>
    /// <exception cref="InvalidOperationException">No assembly: the builder has no definition type and the options name none.</exception>
    public KazoWebApplication UseStaticFiles(Action<KazoStaticFileOptions>? configure = null)
    {
        KazoStaticFileOptions options = new() { Assembly = _builder.DefinitionType?.Assembly };
        configure?.Invoke(options);
        if (options.Assembly is null)
        {
            throw new InvalidOperationException("UseStaticFiles needs the assembly that embeds wwwroot: set options.Assembly, or build the site through its generated host.");
        }

        KazoStaticFileMiddleware middleware = new(options);
        return Use(next => context => middleware.InvokeAsync(context, next));
    }

    /// <summary>Writes <c>Status Code: 404; Not Found</c> for an error status that has no body (ASP.NET's <c>UseStatusCodePages</c>).</summary>
    public KazoWebApplication UseStatusCodePages() => Use(next => async context =>
    {
        await next(context).ConfigureAwait(false);
        KazoHttpResponse response = context.Response;
        if (response.StatusCode is >= 400 and < 600 && response.ContentLength == 0 && response.ContentType is null)
        {
            response.ContentType = "text/plain; charset=utf-8";
            await response.WriteAsync(
                string.Create(CultureInfo.InvariantCulture, $"Status Code: {response.StatusCode}; {ReasonPhrases.Get(response.StatusCode)}"),
                context.RequestAborted).ConfigureAwait(false);
        }
    });

    /// <summary>
    /// Keeps a session per visitor through a cookie (<see cref="KazoSessionOptions.CookieName"/>), in the
    /// site's memory (ADR 0076 §6). Endpoints and later middlewares read <see cref="KazoHttpContext.Session"/>.
    /// </summary>
    /// <param name="configure">Changes the options (cookie name, idle timeout).</param>
    public KazoWebApplication UseCookieSession(Action<KazoSessionOptions>? configure = null)
    {
        KazoSessionOptions options = new();
        configure?.Invoke(options);
        KazoSessionStore store = _services.GetRequiredService<KazoSessionStore>();
        store.IdleTimeout = options.IdleTimeout;
        return Use(next => async context =>
        {
            context.Request.Cookies.TryGetValue(options.CookieName, out string? cookie);
            KazoSession session = store.Load(cookie);
            context.Session = session;
            await next(context).ConfigureAwait(false);
            session.Commit();
            if (session.IsNew && session.IsModified)
            {
                context.Response.Cookies.Append(options.CookieName, session.Id, new KazoCookieOptions
                {
                    Path = options.CookiePath,
                    HttpOnly = true,
                    SameSite = "Lax"
                });
            }
        });
    }

    /// <summary>Maps <paramref name="pattern"/> for every method.</summary>
    /// <param name="pattern">Route template (<c>/health</c>, <c>/files/{*path}</c>).</param>
    /// <param name="handler">The handler.</param>
    public KazoEndpoint Map(string pattern, KazoRequestDelegate handler) => MapMethods(pattern, [], handler);

    /// <summary>Maps <paramref name="pattern"/> for the given methods.</summary>
    /// <param name="pattern">Route template.</param>
    /// <param name="httpMethods">Methods; empty for any.</param>
    /// <param name="handler">The handler.</param>
    public KazoEndpoint MapMethods(string pattern, IReadOnlyList<string> httpMethods, KazoRequestDelegate handler)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(httpMethods);
        ArgumentNullException.ThrowIfNull(handler);
        string methods = httpMethods.Count == 0 ? "*" : string.Join(',', httpMethods);
        KazoEndpoint endpoint = new(KazoRoutePattern.Parse(pattern), httpMethods, handler, $"{methods} {pattern}");
        _endpoints.Add(endpoint);
        return endpoint;
    }

    /// <summary>
    /// Maps <c>GET</c> and <c>HEAD</c> on <paramref name="pattern"/> after every other route
    /// (ASP.NET Core's <c>MapFallback</c>): the Blazor host page of ADR 0084 uses it.
    /// </summary>
    /// <param name="pattern">Route template (<c>/{**path}</c>).</param>
    /// <param name="handler">The handler.</param>
    /// <param name="displayName">A name for logs and the exception page.</param>
    public KazoEndpoint MapFallback(string pattern, KazoRequestDelegate handler, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);
        KazoEndpoint endpoint = new(
            KazoRoutePattern.Parse(pattern), ["GET", "HEAD"], handler, displayName ?? $"GET {pattern} (fallback)", KazoBlazorSiteEndpoints.FallbackOrder);
        _endpoints.Add(endpoint);
        return endpoint;
    }

    /// <summary>Maps <c>GET</c> on <paramref name="pattern"/> (<c>app.MapGet("/health", () =&gt; KazoResults.Ok("up"))</c>).</summary>
    public KazoEndpoint MapGet(string pattern, Func<IKazoActionResult> handler) => MapMethods(pattern, ["GET"], Wrap(handler));

    /// <summary>Maps <c>GET</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapGet(string pattern, Func<KazoHttpContext, IKazoActionResult> handler) => MapMethods(pattern, ["GET"], Wrap(handler));

    /// <summary>Maps <c>GET</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapGet(string pattern, Func<KazoHttpContext, Task<IKazoActionResult>> handler) => MapMethods(pattern, ["GET"], Wrap(handler));

    /// <summary>Maps <c>GET</c> on <paramref name="pattern"/> to a handler that writes the response itself.</summary>
    public KazoEndpoint MapGet(string pattern, KazoRequestDelegate handler) => MapMethods(pattern, ["GET"], handler);

    /// <summary>Maps <c>POST</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapPost(string pattern, Func<KazoHttpContext, IKazoActionResult> handler) => MapMethods(pattern, ["POST"], Wrap(handler));

    /// <summary>Maps <c>POST</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapPost(string pattern, Func<KazoHttpContext, Task<IKazoActionResult>> handler) => MapMethods(pattern, ["POST"], Wrap(handler));

    /// <summary>Maps <c>POST</c> on <paramref name="pattern"/> to a handler that writes the response itself.</summary>
    public KazoEndpoint MapPost(string pattern, KazoRequestDelegate handler) => MapMethods(pattern, ["POST"], handler);

    /// <summary>Maps <c>PUT</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapPut(string pattern, Func<KazoHttpContext, Task<IKazoActionResult>> handler) => MapMethods(pattern, ["PUT"], Wrap(handler));

    /// <summary>Maps <c>PATCH</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapPatch(string pattern, Func<KazoHttpContext, Task<IKazoActionResult>> handler) => MapMethods(pattern, ["PATCH"], Wrap(handler));

    /// <summary>Maps <c>DELETE</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapDelete(string pattern, Func<KazoHttpContext, IKazoActionResult> handler) => MapMethods(pattern, ["DELETE"], Wrap(handler));

    /// <summary>Maps <c>DELETE</c> on <paramref name="pattern"/>.</summary>
    public KazoEndpoint MapDelete(string pattern, Func<KazoHttpContext, Task<IKazoActionResult>> handler) => MapMethods(pattern, ["DELETE"], Wrap(handler));

    /// <summary>
    /// Maps the controllers of the site: every attribute route of the generated table and, unless
    /// <see cref="MapControllerRoute"/> declares conventional routes, the default conventional route
    /// <c>{controller=Home}/{action=Index}/{id?}</c> for the actions without attribute routes (ADR 0076 §3).
    /// </summary>
    public KazoWebApplication MapControllers()
    {
        lock (_gate)
        {
            EnsureNotStarted();
            if (_controllersMapped)
            {
                return this;
            }

            _controllersMapped = true;
        }

        foreach (KazoEndpoint endpoint in KazoActionInvoker.CreateAttributeEndpoints(_builder.Routes))
        {
            _endpoints.Add(endpoint);
        }

        return this;
    }

    /// <summary>Maps a conventional route for the actions without attribute routes (<c>{controller}/{action}/{id?}</c>).</summary>
    /// <param name="name">Route name (for <c>Url.RouteUrl</c>).</param>
    /// <param name="pattern">Template with <c>{controller}</c> and <c>{action}</c> parameters.</param>
    public KazoWebApplication MapControllerRoute(string name, string pattern) => MapControllerRoute(name, pattern, new KazoRouteValueDictionary());

    /// <summary>Maps a conventional route for the actions without attribute routes (<c>blog/{*article}</c>).</summary>
    /// <typeparam name="TDefaults">Type of the defaults (inferred).</typeparam>
    /// <param name="name">Route name (for <c>Url.RouteUrl</c>).</param>
    /// <param name="pattern">Template; <c>{controller}</c> and <c>{action}</c> parameters or <paramref name="defaults"/> name the action.</param>
    /// <param name="defaults">Controller and action when the template has no such parameter (<c>new { controller = "Blog", action = "Article" }</c>).</param>
    public KazoWebApplication MapControllerRoute<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TDefaults>(
        string name,
        string pattern,
        TDefaults defaults)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(pattern);
        int order;
        lock (_gate)
        {
            EnsureNotStarted();
            _conventionalRouteDeclared = true;
            order = _conventionalOrder++;
        }

        foreach (KazoEndpoint endpoint in KazoActionInvoker.CreateConventionalEndpoints(
            _builder.Routes, name, pattern, KazoRouteValueDictionary.FromObject(defaults), order))
        {
            _endpoints.Add(endpoint);
        }

        return this;
    }

    /// <summary>Maps <c>{controller=Home}/{action=Index}/{id?}</c> under the name <c>default</c>.</summary>
    public KazoWebApplication MapDefaultControllerRoute() => MapControllerRoute("default", DefaultConventionalTemplate);

    /// <summary>Serves one request through the pipeline.</summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Signals that the site stops.</param>
    /// <returns>The response (never throws for a failing handler: that is a <c>500</c>).</returns>
    public async Task<KazoWebResponseData> ProcessAsync(KazoWebRequestData request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        KazoRequestDelegate pipeline = GetPipeline();
        // ADR 0088 §5: the request lines of the debug log, as Microsoft.AspNetCore.Hosting.Diagnostics.
        ILogger? requests = _builder.Logging is { DebugLogEnabled: true, LogRequests: true } ? RequestLogger : null;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        requests?.LogInformation("Request starting HTTP/1.1 {Method} {Url} - {ContentType} {ContentLength}",
            request.Method, request.Url.AbsoluteUri, request.ContentType ?? "-", request.Body.Length > 0 ? request.Body.Length : "-");
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        long connection = Interlocked.Increment(ref s_connections);
        KazoHttpContext context = new(
            request,
            scope.ServiceProvider,
            string.Create(CultureInfo.InvariantCulture, $"0HN{request.RemoteEndPoint.Port:X4}{connection:X6}"),
            cancellationToken);
        try
        {
            await pipeline(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            UnhandledException?.Invoke(context, exception);
            ExceptionLogger.LogError(exception, "An unhandled exception has occurred while executing the request ({TraceIdentifier}).", context.TraceIdentifier);
            context.Response.Clear();
            if (_exceptionPage)
            {
                KazoExceptionPage.Write(context, exception);
            }
            else
            {
                // Kestrel without the developer page: a bare 500, the detail goes to the log.
                context.Response.StatusCode = 500;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("500 Internal Server Error", CancellationToken.None).ConfigureAwait(false);
            }
        }

        KazoWebResponseData response = context.Response.ToResponseData(withoutBody: context.Request.Method == "HEAD");
        requests?.LogInformation("Request finished HTTP/1.1 {Method} {Url} - {StatusCode} {ContentLength} {ContentType} {ElapsedMilliseconds}ms",
            request.Method, request.Url.AbsoluteUri, response.StatusCode, response.Body.Length, response.ContentType ?? "-",
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0.0000", CultureInfo.InvariantCulture));
        return response;
    }

    private ILogger RequestLogger => field ??= _services.GetRequiredService<ILoggerFactory>().CreateLogger("Kazo.Os.AppSdk.Web.Hosting.Diagnostics");

    private ILogger ExceptionLogger => field ??= _services.GetRequiredService<ILoggerFactory>().CreateLogger("Kazo.Os.AppSdk.Web.Diagnostics.ExceptionHandler");

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _services.DisposeAsync();

    internal const string DefaultConventionalTemplate = "{controller=Home}/{action=Index}/{id?}";

    private static Func<KazoHttpContext, Task> Run(Func<KazoHttpContext, Task<IKazoActionResult>> handler) => async context =>
    {
        IKazoActionResult result = await handler(context).ConfigureAwait(false);
        await result.ExecuteResultAsync(new KazoActionContext(context)).ConfigureAwait(false);
    };

    private static KazoRequestDelegate Wrap(Func<IKazoActionResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return context => handler().ExecuteResultAsync(new KazoActionContext(context));
    }

    private static KazoRequestDelegate Wrap(Func<KazoHttpContext, IKazoActionResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return context => handler(context).ExecuteResultAsync(new KazoActionContext(context));
    }

    private static KazoRequestDelegate Wrap(Func<KazoHttpContext, Task<IKazoActionResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Func<KazoHttpContext, Task> run = Run(handler);
        return context => run(context);
    }

    private void EnsureNotStarted()
    {
        if (_pipeline is not null)
        {
            throw new InvalidOperationException("The pipeline cannot change once the site serves requests.");
        }
    }

    private KazoRequestDelegate GetPipeline()
    {
        lock (_gate)
        {
            if (_pipeline is not null)
            {
                return _pipeline;
            }

            if (_controllersMapped && !_conventionalRouteDeclared)
            {
                foreach (KazoEndpoint endpoint in KazoActionInvoker.CreateConventionalEndpoints(
                    _builder.Routes, "default", DefaultConventionalTemplate, [], _conventionalOrder++))
                {
                    _endpoints.Add(endpoint);
                }
            }

            KazoEndpointRouter router = _endpoints.GetRouter();
            KazoRequestDelegate pipeline = DispatchAsync;
            for (int index = _middlewares.Count - 1; index >= 0; index--)
            {
                pipeline = _middlewares[index](pipeline);
            }

            KazoRequestDelegate inner = pipeline;
            _pipeline = context =>
            {
                // Routing runs first (ASP.NET's implicit UseRouting): middlewares see the endpoint.
                KazoRouteMatch match = router.Match(context.Request.Method, context.Request.Path);
                context.Items[typeof(KazoRouteMatch)] = match;
                if (match.Endpoint is not null)
                {
                    context.Endpoint = match.Endpoint;
                    context.Request.RouteValues = match.Values;
                }

                return inner(context);
            };
            return _pipeline;
        }
    }

    private static Task DispatchAsync(KazoHttpContext context)
    {
        if (context.Endpoint is KazoEndpoint endpoint)
        {
            return endpoint.Handler(context);
        }

        KazoRouteMatch? match = context.Items.TryGetValue(typeof(KazoRouteMatch), out object? stored) ? stored as KazoRouteMatch : null;
        KazoHttpResponse response = context.Response;
        switch (match?.Kind)
        {
            case KazoRouteMatchKind.MethodNotAllowed:
                response.StatusCode = 405;
                response.Headers["Allow"] = string.Join(", ", match.AllowedMethods);
                break;
            case KazoRouteMatchKind.Options:
                response.StatusCode = 204;
                response.Headers["Allow"] = string.Join(", ", match.AllowedMethods);
                break;
            default:
                response.StatusCode = 404;
                break;
        }

        return Task.CompletedTask;
    }
}
