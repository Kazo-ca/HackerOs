using Kazo.Os.AppSdk.Web.Mvc;
using Kazo.Os.AppSdk.Web.Routing;
using Kazo.Os.AppSdk.Web.Antiforgery;
using Kazo.Os.AppSdk.Web.Sessions;
using Kazo.Os.AppSdk.Web.Views;
using Kazo.Os.AppSdk.Web.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kazo.Os.AppSdk.Web.Hosting;

/// <summary>
/// Builds a site (ASP.NET Core's <c>WebApplicationBuilder</c>, ADR 0076 §2): register services in
/// <see cref="Services"/>, set <see cref="Json"/> and <see cref="Mvc"/>, then <see cref="Build"/> the
/// <see cref="KazoWebApplication"/> and describe its pipeline.
/// </summary>
public sealed class KazoWebApplicationBuilder
{
    private KazoWebApplication? _application;

    /// <summary>Initializes a builder.</summary>
    /// <param name="applicationId">The app id (<c>com.example.intranet</c>).</param>
    /// <param name="applicationName">The display name.</param>
    /// <param name="urls">The URLs the site serves.</param>
    /// <param name="routes">The controller actions, as generated at build (ADR 0082).</param>
    public KazoWebApplicationBuilder(string applicationId, string applicationName, IReadOnlyList<string> urls, KazoControllerRouteTable routes)
        : this(applicationId, applicationName, urls, routes, KazoViewTable.Empty, null)
    {
    }

    /// <summary>Initializes a builder with the site's views.</summary>
    /// <param name="applicationId">The app id (<c>com.example.intranet</c>).</param>
    /// <param name="applicationName">The display name.</param>
    /// <param name="urls">The URLs the site serves.</param>
    /// <param name="routes">The controller actions, as generated at build (ADR 0082).</param>
    /// <param name="views">The views under <c>Views/</c>, as generated at build (ADR 0083).</param>
    /// <param name="definitionType">The site definition's type; its assembly holds the embedded <c>wwwroot</c>.</param>
    public KazoWebApplicationBuilder(
        string applicationId,
        string applicationName,
        IReadOnlyList<string> urls,
        KazoControllerRouteTable routes,
        KazoViewTable views,
        Type? definitionType)
    {
        ArgumentNullException.ThrowIfNull(views);
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        ArgumentNullException.ThrowIfNull(urls);
        ArgumentNullException.ThrowIfNull(routes);
        ApplicationId = applicationId;
        ApplicationName = string.IsNullOrEmpty(applicationName) ? applicationId : applicationName;
        Urls = urls;
        Routes = routes;
        Views = views;
        DefinitionType = definitionType;
    }

    /// <summary>Gets the app id.</summary>
    public string ApplicationId { get; }

    /// <summary>Gets the display name.</summary>
    public string ApplicationName { get; }

    /// <summary>Gets the URLs the site serves.</summary>
    public IReadOnlyList<string> Urls { get; }

    /// <summary>Gets the controller actions of the site.</summary>
    public KazoControllerRouteTable Routes { get; }

    /// <summary>Gets the views of the site (<c>Views/**/*.razor</c>).</summary>
    public KazoViewTable Views { get; }

    /// <summary>Gets the site definition's type, whose assembly embeds <c>wwwroot/</c>; <see langword="null"/> when unknown.</summary>
    public Type? DefinitionType { get; }

    /// <summary>Gets the anti-forgery settings (cookie, field and header names).</summary>
    public KazoAntiforgeryOptions Antiforgery { get; } = new();

    /// <summary>Gets the site's services (<c>Microsoft.Extensions.DependencyInjection</c>); a scope per request.</summary>
    public IServiceCollection Services { get; } = new ServiceCollection();

    /// <summary>Gets the JSON settings.</summary>
    public KazoWebJsonOptions Json { get; } = new();

    /// <summary>Gets the MVC settings (global filters).</summary>
    public KazoMvcOptions Mvc { get; } = new();

    /// <summary>
    /// Gets the logging settings (ADR 0088 §5): <c>ILogger&lt;T&gt;</c> is always injectable;
    /// <c>builder.Logging.EnableDebugLog()</c> writes it to <c>/var/log/hackweb/&lt;app id&gt;.log</c>.
    /// </summary>
    public KazoWebLoggingOptions Logging { get; } = new();

    /// <summary>Gets the application built by <see cref="Build"/>, or <see langword="null"/>.</summary>
    internal KazoWebApplication? Application => _application;

    /// <summary>
    /// Builds the application: adds the framework's services (JSON and MVC options, links, sessions,
    /// view engine, anti-forgery, logging, <see cref="TimeProvider"/>) and creates the service provider. Call it once.
    /// </summary>
    /// <returns>The application, whose pipeline is still to describe.</returns>
    /// <exception cref="InvalidOperationException">Already built.</exception>
    public KazoWebApplication Build()
    {
        if (_application is not null)
        {
            throw new InvalidOperationException("The web application is already built.");
        }

        KazoEndpointDataSource endpoints = new();
        Services.AddSingleton(Json);
        Services.AddSingleton(Mvc);
        Services.AddSingleton(endpoints);
        Services.AddSingleton(new KazoLinkGenerator(endpoints));
        Services.TryAddSingleton(TimeProvider.System);
        Services.TryAddSingleton<KazoSessionStore>();
        Services.AddSingleton(Antiforgery);
        Services.TryAddSingleton<KazoAntiforgery>();
        Services.TryAddSingleton(new KazoViewEngine(Views));
        Services.AddSingleton(Logging);
        // ILogger<T> as in ASP.NET Core; written only when the site enabled its debug log. The OS sink
        // is the logging gateway of the process building the site (Configure runs in the site process).
        IAppExecutionContext? process = KazoExecutionContext.CurrentOrNull;
        Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(Logging.DebugLogEnabled ? Logging.MinimumLevel : LogLevel.None);
            if (Logging.DebugLogEnabled)
            {
                logging.Services.AddSingleton<ILoggerProvider>(provider => new KazoWebDebugLogProvider(
                    provider.GetService<IHackWebDebugLogSink>()
                        ?? (process is null ? new KazoWebDebugLogMemorySink() : new KazoWebDebugLogGatewaySink(process)),
                    provider.GetRequiredService<TimeProvider>()));
            }
        });
        ServiceProvider provider = Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _application = new KazoWebApplication(this, provider, endpoints);
        return _application;
    }
}
