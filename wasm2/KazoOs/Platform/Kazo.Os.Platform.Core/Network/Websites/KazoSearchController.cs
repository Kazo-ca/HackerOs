using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Kazo.Os.Simulation.Abstractions.Network;

namespace Kazo.Os.Platform.Core.Network.Websites;

/// <summary>
/// Simulated search-engine website at hackersearch.net.
/// Mirrors the legacy browser.ts <c>getSearchEnginePage()</c> content
/// as structured typed sections instead of raw HTML strings.
/// </summary>
public sealed class KazoSearchController : SimulatedWebsiteControllerBase
{
    /// <inheritdoc/>
    public override string PrimaryHostname => "hackersearch.net";
    /// <inheritdoc/>
    public override string Theme          => "hacker";
    /// <inheritdoc/>
    public override string SiteName       => "KazoSearch";

    /// <summary>Registers the routes: the home page, GET/POST <c>/search</c>, the JSON <c>/api/search</c> endpoint and <c>/robots.txt</c>.</summary>
    public KazoSearchController()
    {
        Get("/", _ => SimulatedHttpResponse.Ok(BuildHomePage()));
        Get("/search", req =>
        {
            var query = req.Query.TryGetValue("q", out var q) ? q : "";
            return SimulatedHttpResponse.Ok(BuildSearchResultsPage(query));
        });

        // The search form of the structured page posts its fields: answer like a real site,
        // with a redirect to the GET URL so the result page can be bookmarked and refreshed.
        Post("/search", req =>
        {
            var query = req.FormBody?.GetValueOrDefault("q", "") ?? "";
            return SimulatedHttpResponse.Redirect($"/search?q={Uri.EscapeDataString(query)}");
        });

        // ADR 0074 section 1: real bodies. A JSON API and a robots.txt, for curl and scripts.
        Get("/api/search", req =>
        {
            var query = req.Query.TryGetValue("q", out var q) ? q : "";
            // A public API: any page may call it with fetch (CORS, ADR 0075 §3).
            SimulatedHttpResponse response = SimulatedHttpResponse.Json(BuildSearchResultsJson(query));
            return response with { Headers = response.Headers.SetItem("Access-Control-Allow-Origin", "*") };
        });
        Get("/robots.txt", _ => SimulatedHttpResponse.Text(
            "User-agent: *\nDisallow: /api/\n\n# Crawlers welcome. Hackers too.\n"));
    }

    /// <summary>
    /// Builds the JSON answer of <c>/api/search</c>. Written with <see cref="Utf8JsonWriter"/> rather
    /// than reflection-based serialization so the platform stays trim-safe.
    /// </summary>
    internal static string BuildSearchResultsJson(string query)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            writer.WriteNumber("total", 0);
            writer.WriteStartArray("results");
            writer.WriteEndArray();
            writer.WriteStartArray("featured");
            foreach (var (title, url) in FeaturedLinks)
            {
                writer.WriteStartObject();
                writer.WriteString("title", title);
                writer.WriteString("url", url);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static readonly (string Title, string Url)[] FeaturedLinks =
    [
        ("KazoMail", "https://hackmail.com"),
        ("CryptoBank", "https://cryptobank.com"),
        ("Hacker Forum", "https://hackerz.forum"),
    ];

    private static SimulatedPage BuildHomePage() => new(
        Title: "KazoSearch",
        PageTheme: "hacker",
        Sections:
        [
            new HeroSection("KazoSearch", "Search the dark corners of the web"),
            new FormSection(
                Title: "",
                Fields:
                [
                    new FormField("q", "", "search", "Search the web...", false)
                ],
                SubmitLabel: "Search",
                PostPath: "/search"),
            new NavigationSection(
            [
                new NavLink("KazoMail",     "https://hackmail.com"),
                new NavLink("CryptoBank",   "https://cryptobank.com"),
                new NavLink("Hacker Forum", "https://hackerz.forum"),
            ]),
        ]);

    private static SimulatedPage BuildSearchResultsPage(string query) => new(
        Title: $"{query} - KazoSearch",
        PageTheme: "hacker",
        Sections:
        [
            new HeroSection("KazoSearch"),
            new FormSection(
                Title: "",
                Fields:
                [
                    new FormField("q", "", "search", query, false)
                ],
                SubmitLabel: "Search",
                PostPath: "/search"),
            new ListSection(
                Title: $"Search results for: {query}",
                Items:
                [
                    "hackersearch.net - No results found for this query.",
                    "Try a different search term or browse featured links.",
                ]),
        ]);
}
