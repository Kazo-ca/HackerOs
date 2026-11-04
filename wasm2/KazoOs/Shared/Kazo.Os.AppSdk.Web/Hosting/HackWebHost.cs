using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.AppSdk.Web.Views;
using Kazo.Os.Simulation.Abstractions.Diagnostics;

namespace Kazo.Os.AppSdk.Web.Hosting;

/// <summary>
/// The process of an ASP.Hack site (ADR 0076 §1): a <see cref="WebServerAppBase"/> that listens on the
/// site's URLs, builds the <see cref="HackWebApplication"/> from the definition's
/// <see cref="WebAppDefinition.Configure"/> once the socket is open, and passes every request to it.
/// </summary>
/// <remarks>
/// The route generator writes one subclass per <c>partial</c> definition (<c>IntranetAppWebHost</c>),
/// which is the definition's entry point. The OS starts it on demand in the shared web worker, under
/// the identity of the first caller (ADR 0074 §3); stopping it forgets its sessions.
/// </remarks>
public abstract class HackWebHost : WebServerAppBase
{
    private readonly WebAppDefinition _definition;
    private readonly HackControllerRouteTable _routes;
    private readonly HackViewTable _views;
    private HackWebApplication? _application;

    /// <summary>Initializes the site process.</summary>
    /// <param name="manifest">The site's validated manifest.</param>
    /// <param name="definition">A new instance of the site's definition.</param>
    /// <param name="routes">The controller actions generated for the site's assembly.</param>
    protected HackWebHost(AppManifest manifest, WebAppDefinition definition, HackControllerRouteTable routes)
        : this(manifest, definition, routes, HackViewTable.Empty)
    {
    }

    /// <summary>Initializes the site process with its views.</summary>
    /// <param name="manifest">The site's validated manifest.</param>
    /// <param name="definition">A new instance of the site's definition.</param>
    /// <param name="routes">The controller actions generated for the site's assembly.</param>
    /// <param name="views">The views generated for the site's assembly (ADR 0083).</param>
    protected HackWebHost(AppManifest manifest, WebAppDefinition definition, HackControllerRouteTable routes, HackViewTable views)
        : base(manifest)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(views);
        _definition = definition;
        _routes = routes;
        _views = views;
    }

    /// <summary>Gets the running application, once built.</summary>
    public HackWebApplication? Application => _application;

    /// <summary>Builds the application of <paramref name="definition"/> (also used by tests to run a site without the OS).</summary>
    /// <param name="definition">The site definition.</param>
    /// <param name="routes">The generated route table.</param>
    /// <param name="urls">The URLs; the definition's when <see langword="null"/>.</param>
    /// <param name="views">The generated view table; none when <see langword="null"/>.</param>
    /// <returns>The application, with its pipeline described.</returns>
    /// <exception cref="InvalidOperationException"><see cref="WebAppDefinition.Configure"/> did not call <c>Build</c>.</exception>
    public static HackWebApplication CreateApplication(WebAppDefinition definition, HackControllerRouteTable routes, IReadOnlyList<string>? urls = null, HackViewTable? views = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(routes);
        HackWebApplicationBuilder builder = new(definition.Id, definition.Name, urls ?? definition.Urls, routes, views ?? HackViewTable.Empty, definition.GetType());
        definition.Configure(builder);
        HackWebApplication application = builder.Application
            ?? throw new InvalidOperationException($"{definition.GetType().Name}.Configure must call builder.Build().");
        // ADR 0084 section 2: a site with a Blazor root also answers as a Blazor WebAssembly site,
        // after its own routes.
        HackBlazorSiteEndpoints.Map(application, definition);
        return application;
    }

    /// <inheritdoc />
    protected override Task OnListeningAsync(HackHttpListener listener, CancellationToken cancellationToken)
    {
        HackWebApplication application = CreateApplication(_definition, _routes, Urls, _views);
        application.UnhandledException += (context, exception) =>
            HackExecutionContext.Current.Logging.Log(
                DiagnosticSeverity.Error,
                $"{context.Request.Method} {context.Request.PathAndQuery} ({context.TraceIdentifier}): {exception.GetType().Name}: {exception.Message}");
        _application = application;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ProcessRequestAsync(HackHttpListenerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        HackWebApplication application = _application ?? throw new InvalidOperationException("The site is not started.");
        HackHttpListenerRequest request = context.Request;
        List<KeyValuePair<string, string>> headers = [];
        foreach (string? name in request.Headers.AllKeys)
        {
            if (name is null)
            {
                continue;
            }

            foreach (string value in request.Headers.GetValues(name) ?? [])
            {
                headers.Add(new(name, value));
            }
        }

        HackWebRequestData data = new(request.HttpMethod, request.Url, headers, request.ContentType, request.Body)
        {
            RemoteEndPoint = request.RemoteEndPoint,
            LocalEndPoint = request.LocalEndPoint
        };

        HackWebResponseData reply = await application.ProcessAsync(data, cancellationToken).ConfigureAwait(false);
        HackHttpListenerResponse response = context.Response;
        response.StatusCode = reply.StatusCode;
        response.StatusDescription = reply.ReasonPhrase;
        response.ContentType = reply.ContentType;
        foreach (KeyValuePair<string, string> header in reply.Headers)
        {
            response.AppendHeader(header.Key, header.Value);
        }

        response.Close(reply.Body, willBlock: false);
    }

    /// <inheritdoc />
    protected override async ValueTask OnStoppingAsync(ServiceStopReason reason, CancellationToken cancellationToken)
    {
        if (_application is HackWebApplication application)
        {
            _application = null;
            await application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
