using System.Collections.Concurrent;
using Kazo.Os.Apps.Browser.Bridge;
using Kazo.Os.Apps.Browser.DevTools;
using Kazo.Os.Apps.Browser.Net;
using Kazo.Os.Apps.Browser.Storage;
using Xunit;

namespace Kazo.Os.Apps.Browser.Tests;

public sealed class PageStorageTests
{
    private sealed class MemoryFiles : IBrowserStorageFiles
    {
        public ConcurrentDictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.TryGetValue(path, out string? text) ? text : null);

        public Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default)
        {
            Files[path] = text;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Local_storage_is_read_from_and_written_to_the_site_file()
    {
        MemoryFiles files = new();
        string root = PageStorage.GetRoot("/home/neo");
        files.Files[$"{root}/https_intranet.hack/local-storage.json"] = "{\"theme\":\"dark\"}";
        PageStorage storage = new(files, root);

        IReadOnlyDictionary<string, string> loaded = await storage.GetLocalAsync("https_intranet.hack");
        storage.Apply("https_intranet.hack", new BridgeStorageEvent("local", "set", "lang", "fr"));
        storage.Apply("https_intranet.hack", new BridgeStorageEvent("local", "remove", "theme", null));
        await storage.FlushAsync();

        Assert.Equal("/home/neo/.local/share/hackeros/browser/storage", root);
        Assert.Equal("dark", loaded["theme"]);
        Assert.Equal("{\"lang\":\"fr\"}", files.Files[$"{root}/https_intranet.hack/local-storage.json"]);
        Assert.Equal("fr", (await storage.GetLocalAsync("https_intranet.hack"))["lang"]);
    }

    [Fact]
    public async Task Session_storage_stays_in_memory_and_clear_empties_an_area()
    {
        MemoryFiles files = new();
        PageStorage storage = new(files, "/home/neo/.local/share/hackeros/browser/storage");

        storage.Apply("file", new BridgeStorageEvent("session", "set", "step", "2"));
        Assert.Equal("2", storage.GetSession("file")["step"]);
        storage.Apply("file", new BridgeStorageEvent("session", "clear", null, null));
        await storage.FlushAsync();

        Assert.Empty(storage.GetSession("file"));
        Assert.Empty(files.Files);
    }

    [Fact]
    public async Task A_corrupt_file_is_reported_and_treated_as_empty()
    {
        MemoryFiles files = new();
        List<string> errors = [];
        PageStorage storage = new(files, "/s", errors.Add);
        files.Files["/s/k/local-storage.json"] = "{not json";

        Assert.Empty(await storage.GetLocalAsync("k"));
        Assert.Single(errors);
    }
}

public sealed class BrowserPiecesTests
{
    [Fact]
    public void Error_pages_name_the_host_and_the_code()
    {
        BrowserErrorView dns = BrowserErrorPages.ForNetworkError("ERR_NAME_NOT_RESOLVED", new Uri("https://intranet.hack/"));
        BrowserErrorView refused = BrowserErrorPages.ForNetworkError("ERR_CONNECTION_REFUSED", new Uri("http://localhost:8080/"));
        BrowserErrorView file = BrowserErrorPages.ForNetworkError(BrowserErrorCodes.FileNotFound, new Uri("file:///home/neo/x.html"));
        BrowserErrorView notFound = BrowserErrorPages.ForHttpStatus(404, new Uri("https://intranet.hack/missing"));
        BrowserErrorView server = BrowserErrorPages.ForHttpStatus(503, new Uri("https://intranet.hack/"));

        Assert.Equal("intranet.hack's server IP address could not be found.", dns.Message);
        Assert.Equal("localhost refused to connect.", refused.Message);
        Assert.Contains("netstat -tlnp", refused.Hints[0], StringComparison.Ordinal);
        Assert.Contains("/home/neo/x.html", file.Message, StringComparison.Ordinal);
        Assert.Equal("HTTP ERROR 404", notFound.Code);
        Assert.Equal("This intranet.hack page can't be found", notFound.Title);
        Assert.Equal("This page isn't working", server.Title);
    }

    [Fact]
    public void Directory_listing_links_entries_relative_to_the_folder()
    {
        string html = DirectoryListingPage.Build("/home/neo/site", [
            new("index.html", false, 1500, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)),
            new("css", true, null, new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc)),
            new("a <b>.txt", false, 12, DateTime.UnixEpoch),
        ]);

        Assert.Contains("<title>Index of /home/neo/site/</title>", html, StringComparison.Ordinal);
        Assert.Contains("href=\"../\"", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("css/", StringComparison.Ordinal) < html.IndexOf("index.html", StringComparison.Ordinal));
        Assert.Contains("href=\"a%20%3Cb%3E.txt\"", html, StringComparison.Ordinal);
        Assert.Contains("a &lt;b&gt;.txt", html, StringComparison.Ordinal);
        Assert.Contains("1.5 kB", html, StringComparison.Ordinal);
        Assert.DoesNotContain("../", DirectoryListingPage.Build("/", []), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://files.hack/dl/report%202026.pdf", null, "report 2026.pdf")]
    [InlineData("https://files.hack/dl?id=3", "attachment; filename=\"data.csv\"", "data.csv")]
    [InlineData("https://files.hack/", null, "download")]
    [InlineData("https://files.hack/x", "attachment; filename=\"../../etc/passwd\"", "passwd")]
    public void Download_names_follow_content_disposition_then_the_path(string url, string? disposition, string expected) =>
        Assert.Equal(expected, BrowserDownloads.SuggestFileName(new Uri(url), disposition));

    [Fact]
    public async Task Download_paths_get_a_number_when_taken()
    {
        HashSet<string> existing = ["/home/neo/Downloads/a.zip", "/home/neo/Downloads/a (1).zip"];

        string path = await BrowserDownloads.GetUniquePathAsync("/home/neo/Downloads", "a.zip", candidate => Task.FromResult(existing.Contains(candidate)));

        Assert.Equal("/home/neo/Downloads/a (2).zip", path);
    }

    [Fact]
    public void Developer_tools_log_is_bounded_and_counts_errors()
    {
        DevToolsLog log = new();
        int changes = 0;
        log.Changed += () => changes++;

        for (int index = 0; index < DevToolsLog.MaxEntries + 10; index++)
        {
            log.Add(new ConsoleLogEntry(index % 2 == 0 ? ConsoleLevel.Error : ConsoleLevel.Log, $"m{index}", string.Empty));
        }

        Assert.Equal(DevToolsLog.MaxEntries, log.Console.Count);
        Assert.Equal("m10", log.Console[0].Message);
        Assert.Equal(DevToolsLog.MaxEntries / 2, log.ErrorCount);
        log.Clear();
        Assert.Empty(log.Console);
        Assert.Equal(DevToolsLog.MaxEntries + 11, changes);
        Assert.Equal("app.js?v=2", DevToolsPanel.ShortName("https://site.hack/js/app.js?v=2"));
        Assert.Equal("site.hack", DevToolsPanel.ShortName("https://site.hack/"));
    }

    [Fact]
    public void Cors_check_follows_the_fetch_standard()
    {
        BrowserResponse Response(params (string, string)[] headers) =>
            new(200, "OK", new Uri("https://api.hack/"), [.. headers.Select(header => new KeyValuePair<string, string>(header.Item1, header.Item2))], []);

        Assert.False(PageBridgeHandler.CorsAllows(Response(), "https://a.hack", false, out _));
        Assert.True(PageBridgeHandler.CorsAllows(Response(("Access-Control-Allow-Origin", "*")), "https://a.hack", false, out _));
        Assert.False(PageBridgeHandler.CorsAllows(Response(("Access-Control-Allow-Origin", "https://b.hack")), "https://a.hack", false, out string? reason));
        Assert.Contains("not equal to the supplied origin", reason, StringComparison.Ordinal);
        Assert.False(PageBridgeHandler.CorsAllows(Response(("Access-Control-Allow-Origin", "https://a.hack")), "https://a.hack", true, out _));
        Assert.True(PageBridgeHandler.CorsAllows(Response(("Access-Control-Allow-Origin", "https://a.hack"), ("Access-Control-Allow-Credentials", "true")), "https://a.hack", true, out _));
    }
}
