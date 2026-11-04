using System.Text;
using System.Text.Json;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Routing;

namespace Kazo.Os.AppSdk.Web.Hosting;

/// <summary>
/// What a site with a Blazor root answers besides its MVC routes (ADR 0084, section 2), as a real
/// Blazor WebAssembly site served by ASP.NET Core does: the host page for every path without an
/// extension (<c>MapFallbackToFile("index.html")</c>), the loader script and the boot manifest. All
/// three come last in the routing order, so the site's own routes win.
/// </summary>
public static class KazoBlazorSiteEndpoints
{
    /// <summary>The order of the fallback endpoints: after every attribute and conventional route.</summary>
    public const int FallbackOrder = 1_000_000;

    /// <summary>Maps the three Blazor endpoints of <paramref name="definition"/> on <paramref name="application"/>.</summary>
    /// <param name="application">The site's application, before its first request.</param>
    /// <param name="definition">The site definition; nothing is mapped when it has no <see cref="App.Abstractions.AppDefinition.BlazorRoot"/>.</param>
    public static void Map(KazoWebApplication application, WebAppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.BlazorRoot is not { } root)
        {
            return;
        }

        KazoBlazorBootManifest boot = new()
        {
            AppId = definition.Id,
            AppName = definition.Name,
            EntryAssembly = root.Assembly.GetName().Name + ".dll",
            RootComponent = root.FullName ?? root.Name
        };
        byte[] bootJson = JsonSerializer.SerializeToUtf8Bytes(boot, KazoBlazorBootJsonContext.Default.KazoBlazorBootManifest);
        byte[] hostPage = Encoding.UTF8.GetBytes(HostPage(definition.Name));
        byte[] script = Encoding.UTF8.GetBytes(LoaderScript);

        application.MapFallback(
            "/" + KazoBlazorBootManifest.BootPath,
            context => WriteAsync(context, "application/json; charset=utf-8", bootJson),
            "GET blazor.boot.json (Blazor site)");
        application.MapFallback(
            "/" + KazoBlazorBootManifest.ScriptPath,
            context => WriteAsync(context, "text/javascript; charset=utf-8", script),
            "GET blazor.hack.js (Blazor site)");
        application.MapFallback(
            "/{**path}",
            context =>
            {
                // A path with an extension is a file the site does not have: 404, not the host page.
                string last = context.Request.Path.AsSpan().TrimEnd('/').ToString();
                int slash = last.LastIndexOf('/');
                if (slash >= 0)
                {
                    last = last[(slash + 1)..];
                }

                if (last.Contains('.'))
                {
                    context.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                return WriteAsync(context, "text/html; charset=utf-8", hostPage);
            },
            "GET {**path} → host page (Blazor site)");
    }

    /// <summary>Tells whether an HTML document is the host page of a Blazor site: it loads the loader script.</summary>
    /// <param name="html">The document.</param>
    public static bool IsHostPage(string html) =>
        html is not null && html.Contains(KazoBlazorBootManifest.ScriptPath, StringComparison.OrdinalIgnoreCase);

    private static Task WriteAsync(KazoHttpContext context, string contentType, byte[] body)
    {
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }

    /// <summary>The host page (a Blazor WebAssembly <c>index.html</c>): an empty <c>app</c> element and the loader.</summary>
    private static string HostPage(string name) => $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>{System.Net.WebUtility.HtmlEncode(name)}</title>
            <base href="/" />
        </head>
        <body>
            <div id="app">Loading...</div>
            <script src="{KazoBlazorBootManifest.ScriptPath}"></script>
        </body>
        </html>

        """;

    /// <summary>
    /// The loader script. The Kazo OS browser never runs it (it starts the site itself); any other
    /// client that runs it only learns that the site needs the Kazo OS browser.
    /// </summary>
    private const string LoaderScript = """
        // blazor.hack.js: the loader of an interactive Blazor site of Kazo OS (ADR 0084).
        // The Kazo OS browser recognises this script, reads _framework/blazor.boot.json and draws the
        // site's root component itself; a page that runs this script is outside that browser.
        (function () {
            var app = document.getElementById("app");
            if (app) {
                app.textContent = "This site is an interactive Kazo OS site: open it in the Kazo OS browser.";
            }
        })();

        """;
}
