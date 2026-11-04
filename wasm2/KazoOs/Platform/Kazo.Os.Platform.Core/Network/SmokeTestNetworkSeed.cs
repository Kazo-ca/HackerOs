using System.Collections.Immutable;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network;

/// <summary>
/// Minimal, explicitly-labeled seed data for the simulated network (ADR 0034) — just enough for
/// <c>curl</c>/<c>ping</c>/<c>nmap</c> to be smoke-tested end-to-end now that they're wired into the
/// app catalog. This is deliberately not an attempt at a full simulated internet, which is
/// out of scope here (no game in this repository, ADR 0135) — every
/// hostname here uses the <c>.hackeros</c> suffix the server-side proxy already treats as a
/// simulated-only domain (<c>ProxyService</c>'s blocked-suffix list), so it's unambiguous that
/// these hosts are smoke-test fixtures, never real network destinations.
/// </summary>
public static class SmokeTestNetworkSeed
{
    /// <summary>Gets the seeded hosts.</summary>
    public static IReadOnlyList<SimulatedHost> Hosts { get; } =
    [
        new SimulatedHost(
            Ip: "10.0.0.10",
            Hostname: "example.hackeros",
            IsUp: true,
            LatencyMs: 12,
            Ports:
            [
                new SimulatedPort(22, SimulatedPortState.Filtered, new SimulatedPortService("ssh")),
                new SimulatedPort(80, SimulatedPortState.Open, new SimulatedPortService("http", "1.1")),
                new SimulatedPort(443, SimulatedPortState.Open, new SimulatedPortService("https", "1.1"))
            ]),
        new SimulatedHost(
            Ip: "10.0.0.20",
            Hostname: "empty.hackeros",
            IsUp: true,
            LatencyMs: 30,
            Ports: [new SimulatedPort(443, SimulatedPortState.Open, new SimulatedPortService("https"))])
    ];

    /// <summary>Gets the seeded website controllers — only <c>example.hackeros</c> serves content.</summary>
    public static IReadOnlyList<ISimulatedWebsiteController> Websites { get; } =
    [
        new ExampleWebsiteController()
    ];

    private sealed class ExampleWebsiteController : ISimulatedWebsiteController
    {
        public string PrimaryHostname => "example.hackeros";
        public IReadOnlyCollection<string> AliasHostnames => [];
        public string Theme => "hacker";
        public string SiteName => "Example (smoke test)";

        public SimulatedHttpResponse ProcessRequest(SimulatedHttpRequest request) => request.Path switch
        {
            // ADR 0075: a real HTML page with a style sheet, a script and a JSON API, so the
            // browser's isolated frame and its bridge are smoke-tested end to end.
            "/page.html" => SimulatedHttpResponse.Html(PageHtml),
            "/site.css" => SimulatedHttpResponse.Content(SimulatedHttpBody.FromText(PageCss, SimulatedMediaTypes.TextCss)),
            "/site.js" => SimulatedHttpResponse.Content(SimulatedHttpBody.FromText(PageScript, SimulatedMediaTypes.TextJavaScript)),
            "/form.html" => SimulatedHttpResponse.Html(FormHtml),
            "/echo" when request.Method == SimulatedHttpMethods.Post => SimulatedHttpResponse.Html(
                "<!DOCTYPE html><html><head><title>Echo</title></head><body><p id=\"echo\">msg="
                + System.Net.WebUtility.HtmlEncode(request.FormBody?.GetValueOrDefault("msg", "") ?? "")
                + "</p></body></html>"),
            "/api/ping" => WithHeader(SimulatedHttpResponse.Json("{\"pong\":true,\"host\":\"example.hackeros\"}"), "Access-Control-Allow-Origin", "*"),
            _ => StructuredPage(),
        };

        private const string PageHtml = """
            <!DOCTYPE html>
            <html>
            <head><title>Example HTML page</title><link rel="stylesheet" href="/site.css"></head>
            <body>
            <h1 id="title">Example HTML page</h1>
            <p id="script-status">script not run</p>
            <p id="fetch-status">fetch pending</p>
            <p id="cookie-status"></p>
            <script src="/site.js"></script>
            </body>
            </html>
            """;

        private const string FormHtml = """
            <!DOCTYPE html>
            <html>
            <head><title>Example form</title></head>
            <body>
            <form method="post" action="/echo"><input id="msg" name="msg" value="hello bridge"><button id="send" type="submit">Send</button></form>
            <p id="xhr-status">xhr pending</p>
            <script>
            const xhr = new XMLHttpRequest();
            xhr.open("GET", "/api/ping");
            xhr.responseType = "json";
            xhr.onload = () => { document.getElementById("xhr-status").textContent = xhr.status + " " + (xhr.response && xhr.response.pong ? "xhr pong" : "xhr no pong"); };
            xhr.onerror = () => { document.getElementById("xhr-status").textContent = "xhr failed"; };
            xhr.send();
            </script>
            </body>
            </html>
            """;

        private const string PageCss = "body { background: #101820; color: #c0ffc0; } #title { color: rgb(255, 0, 0); }";

        private const string PageScript = """
            document.getElementById("script-status").textContent = "script ran";
            document.cookie = "visited=yes";
            document.getElementById("cookie-status").textContent = document.cookie;
            fetch("/api/ping")
                .then((response) => response.json())
                .then((body) => { document.getElementById("fetch-status").textContent = body.pong ? "pong" : "no pong"; })
                .catch((error) => { document.getElementById("fetch-status").textContent = "fetch failed: " + error; });
            """;

        private static SimulatedHttpResponse WithHeader(SimulatedHttpResponse response, string name, string value) =>
            response with { Headers = response.Headers.SetItem(name, value) };

        private SimulatedHttpResponse StructuredPage() =>
            SimulatedHttpResponse.Ok(new SimulatedPage(
                Title: "Example",
                PageTheme: Theme,
                Sections: ImmutableArray.Create<SimulatedPageSection>(
                    new HeroSection("Welcome to example.hackeros", "A minimal smoke-test host."),
                    new ParagraphSection(
                        "This page exists only to prove curl/ping/nmap work end-to-end (ADR 0034). " +
                        "It is not real site content."))));
    }
}
