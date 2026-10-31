namespace HackerOs.Server.Endpoints;

/// <summary>
/// Sends the OS's page for every page address of the OS (ADR 0130). The OS is a single page that
/// routes itself in the browser, so <c>/</c>, a deep link or a reload must all receive the
/// Ecosystem's <c>index.html</c>. The request is rewritten to <c>/index.html</c> before routing, so
/// the file is served by <c>MapStaticAssets</c> with its fingerprinted references and compression,
/// in every environment (<c>MapFallbackToFile</c> only finds it in a published or Development host).
/// The page's scoped-CSS bundle is mapped the same way (<see cref="ServerStylesPath"/>).
/// </summary>
public static class OsPageRewrite
{
    /// <summary>The OS's page.</summary>
    public const string PagePath = "/index.html";

    /// <summary>The scoped-CSS bundle the Ecosystem's <c>index.html</c> links.</summary>
    public const string EcosystemStylesPath = "/HackerOs.Ecosystem.styles.css";

    /// <summary>
    /// This server's own scoped-CSS bundle. The Razor SDK never flows a project's app bundle
    /// (<c>{Project}.styles.css</c>, an asset of the current project only) to a project that
    /// references it, so the Ecosystem's bundle is not among the static web assets here. The
    /// server's bundle imports the scoped CSS of the same projects (the server references only the
    /// Ecosystem, whose references are the whole OS), so it is served in its place.
    /// </summary>
    public const string ServerStylesPath = "/HackerOs.Server.styles.css";

    /// <summary>Paths the server answers itself; they never receive the page, even when unknown (a 404 stays a 404).</summary>
    private static readonly string[] ServerPrefixes = ["/api", "/health", "/openapi", "/scalar", "/_framework", "/_content"];

    /// <summary>Adds the rewrite. Must run before <c>UseRouting</c>.</summary>
    public static IApplicationBuilder UseOsPageRewrite(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (IsOsPage(context.Request.Method, context.Request.Path))
            {
                context.Request.Path = PagePath;
            }
            else if (context.Request.Path.Equals(EcosystemStylesPath, StringComparison.OrdinalIgnoreCase))
            {
                context.Request.Path = ServerStylesPath;
            }

            return next(context);
        });

    /// <summary>
    /// Serves the published <c>wwwroot/index.html</c> when <c>MapStaticAssets</c> has no endpoint for
    /// it. A published server (<c>dotnet publish</c>, the installable server of ADR 0191) gets the
    /// Ecosystem's page with its asset placeholders already resolved
    /// (<c>OverrideHtmlAssetPlaceholders</c>) as a plain file, without an entry in the static asset
    /// endpoints, so <c>/</c> would answer 404 there. Must run after <c>UseRouting</c>: a page an
    /// endpoint already serves (a build, Development) is left to it.
    /// </summary>
    public static IApplicationBuilder UseOsPageFile(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is null
                && context.Request.Path.Equals(PagePath, StringComparison.OrdinalIgnoreCase)
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider.GetFileInfo(PagePath) is { Exists: true } page)
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                // The page names the fingerprinted files of this version: never keep an old one.
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.ContentLength = page.Length;
                if (HttpMethods.IsGet(context.Request.Method))
                {
                    await context.Response.SendFileAsync(page, context.RequestAborted);
                }

                return;
            }

            await next(context);
        });

    /// <summary>Gets whether a request asks for a page of the OS rather than a file or a server path.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Request path.</param>
    public static bool IsOsPage(string method, PathString path)
    {
        if (!(HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
        {
            return false;
        }

        string value = path.Value ?? "/";
        foreach (string prefix in ServerPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // A file (anything with an extension in its last segment) is served or 404s as itself.
        string lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return !lastSegment.Contains('.', StringComparison.Ordinal);
    }
}
