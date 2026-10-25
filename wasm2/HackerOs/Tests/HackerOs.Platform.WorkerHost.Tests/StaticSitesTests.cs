using System.Text.RegularExpressions;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.AppSdk.IO;
using HackerOs.Simulation.Abstractions.FileSystem;

namespace HackerOs.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0077 and ADR 0080 through a whole OS and the real commands: <c>hackwebd</c> started with
/// <c>systemctl</c>, user and system <c>.site</c> files, static files with their types, default
/// documents, <c>404</c>/<c>403</c>/<c>304</c>, hot reload, <c>hackdns</c> publishing a bound name,
/// the <c>www-data</c> rule, the privileged-port rule of user sites and the logs in
/// <c>/var/log/hackweb</c>. With workers, the clients (<c>curl</c>, <c>nslookup</c>) run in Web Workers.
/// </summary>
public sealed class StaticSitesTests
{
    private const string Index = "<!doctype html><title>docs</title><h1>Bonjour</h1>\n";

    public static TheoryData<bool> Modes => new() { false, true };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_user_site_serves_its_files_with_their_type_default_document_and_a_404(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await WriteUserSiteAsync(os, "docs", """["http://localhost:8080/", "http://docs.hack:8080/"]""");
        await StartHackwebdAsync(os, 8080, bindings: 2);

        ShellRun index = await os.RunAsync("curl -i http://localhost:8080/").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun css = await os.RunAsync("curl -i http://localhost:8080/css/site.css").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun byName = await os.RunAsync("curl http://docs.hack:8080/").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun missing = await os.RunAsync("curl -i http://localhost:8080/nope.html").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun lookup = await os.RunAsync("nslookup docs.hack").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(index.ExitCode == 0, index.Stderr);
        Assert.Contains("HTTP/1.1 200", index.Stdout, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/html; charset=utf-8", index.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Server: hackwebd/1.0", index.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Index, index.Stdout, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/css; charset=utf-8", css.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("h1 { color: lime; }\n", css.Stdout, StringComparison.Ordinal);
        Assert.Equal(Index, byName.Stdout);
        Assert.Contains("HTTP/1.1 404", missing.Stdout, StringComparison.Ordinal);
        Assert.Contains("<center>hackwebd/1.0</center>", missing.Stdout, StringComparison.Ordinal);
        Assert.Contains(";; source: hosted site docs (this machine)\nName:\tdocs.hack\nAddress: 127.0.0.1\n", lookup.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task An_unchanged_file_answers_304_to_its_etag_and_a_directory_needs_browsing_to_be_listed(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await WriteUserSiteAsync(os, "docs", """["http://localhost:8080/"]""");
        await StartHackwebdAsync(os, 8080);

        ShellRun first = await os.RunAsync("curl -i http://localhost:8080/index.html").WaitAsync(TimeSpan.FromSeconds(20));
        string etag = Regex.Match(first.Stdout, "^ETag: (\"[^\"]+\")", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value;
        ShellRun again = await os.RunAsync($"curl -i -H 'If-None-Match: {etag}' http://localhost:8080/index.html").WaitAsync(TimeSpan.FromSeconds(20));
        // The slash is added by a 301, which curl follows; no index.html in /css and no browsing: 403.
        ShellRun forbidden = await os.RunAsync("curl -i http://localhost:8080/css").WaitAsync(TimeSpan.FromSeconds(20));

        await WriteUserSiteAsync(os, "docs", """["http://localhost:8080/"]""", directoryBrowsing: true);
        ShellRun listing = await WaitForAsync(os, "curl http://localhost:8080/css/", run => run.Stdout.Contains("Index of /css/", StringComparison.Ordinal));

        Assert.NotEqual(string.Empty, etag);
        Assert.Contains("HTTP/1.1 304", again.Stdout, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 403", forbidden.Stdout, StringComparison.Ordinal);
        Assert.Contains("<a href=\"site.css\">site.css</a>", listing.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Sites_apply_hot_and_a_user_site_cannot_take_a_privileged_port(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await os.Log.AttachAsync(os.FileSystem);
        await WriteUserSiteAsync(os, "docs", """["http://localhost:8080/"]""");
        await StartHackwebdAsync(os, 8080);

        // A second site, written while hackwebd runs, is served without a restart.
        await WriteUserSiteAsync(os, "blog", """["http://localhost:8081/"]""");
        await OsFixture.WaitUntilAsync(() => os.Ports.Snapshot().Any(row => row.Port == 8081), because: "hackwebd binds the new site");
        // Disabled: its socket closes.
        await WriteUserSiteAsync(os, "blog", """["http://localhost:8081/"]""", enabled: false);
        await OsFixture.WaitUntilAsync(() => os.Ports.Snapshot().All(row => row.Port != 8081), because: "a disabled site closes its socket");
        // A user site on port 80 is refused.
        await WriteUserSiteAsync(os, "intra", """["http://intra.hack/"]""");
        ShellRun refused = await WaitForAsync(os, "cat /var/log/hackweb/error.log", run => run.Stdout.Contains("intra:", StringComparison.Ordinal), flush: true);

        await os.RunAsync("curl http://localhost:8080/").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun access = await WaitForAsync(os, "cat /var/log/hackweb/docs.access.log", run => run.Stdout.Contains("\"GET / HTTP/1.1\" 200", StringComparison.Ordinal), flush: true);

        Assert.DoesNotContain(os.Ports.Snapshot(), row => row.Port == 80);
        Assert.Contains("port 80 of 'http://intra.hack/' is privileged", refused.Stdout, StringComparison.Ordinal);
        Assert.Contains("[notice]", refused.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"^127\.0\.0\.1 - - \[16/Oct/2026:\d\d:\d\d:\d\d \+0000\] ""GET / HTTP/1\.1"" 200 \d+ ""-"" ""curl/", access.Stdout);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task A_system_site_takes_port_80_and_is_served_as_www_data(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);
        await RootCreateDirectoryAsync(os.FileSystem, "/var/www");
        await RootCreateDirectoryAsync(os.FileSystem, "/var/www/html");
        await RootWriteAsync(os.FileSystem, "/var/www/html/index.html", "<h1>intranet</h1>\n", 0b110_100_100);
        await RootWriteAsync(os.FileSystem, "/var/www/html/secret.txt", "root only\n", 0b110_000_000);
        await WriteUserFilesAsync(os);
        await RootWriteAsync(os.FileSystem, "/etc/hackweb/sites/intranet.site", SiteJson("intranet", "/var/www/html", """["http://intranet.hack/"]"""), 0b110_100_100);
        // An administrator's site rooted in a 0700 home: www-data cannot enter it.
        await RootWriteAsync(os.FileSystem, "/etc/hackweb/sites/homedocs.site", SiteJson("homedocs", "/home/alice/www", """["http://homedocs.hack/"]"""), 0b110_100_100);
        await StartHackwebdAsync(os, 80, bindings: 2);

        ShellRun page = await os.RunAsync("curl http://intranet.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun secret = await os.RunAsync("curl -i http://intranet.hack/secret.txt").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun home = await os.RunAsync("curl -i http://homedocs.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun netstat = await os.RunAsync("netstat -tlnp").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("<h1>intranet</h1>\n", page.Stdout);
        Assert.Contains("HTTP/1.1 403", secret.Stdout, StringComparison.Ordinal);
        Assert.Contains("HTTP/1.1 403", home.Stdout, StringComparison.Ordinal);
        Assert.Contains($"0.0.0.0:80              0.0.0.0:*               LISTEN      {os.FindPid(HackerOs.Apps.WebServer.HackWebdApp.AppId)}/hackwebd", netstat.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Systemctl_stop_frees_the_ports_and_the_name_stays_published()
    {
        await using OsFixture os = await StartAsync(workers: false);
        await WriteUserSiteAsync(os, "docs", """["http://docs.hack:8080/"]""");
        await StartHackwebdAsync(os, 8080);

        Task<ShellRun> stopping = os.RunAsync("systemctl stop hackwebd");
        for (int second = 0; second < 20 && !stopping.IsCompleted; second++)
        {
            await Task.WhenAny(stopping, Task.Delay(50));
            os.Clock.Advance();
        }

        ShellRun stop = await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        ShellRun refused = await os.RunAsync("curl http://docs.hack:8080/").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(0, stop.ExitCode);
        Assert.DoesNotContain(os.Ports.Snapshot(), row => row.Port == 8080);
        // The name still resolves (the .site file is there); nothing listens: connection refused.
        Assert.Equal(7, refused.ExitCode);
    }

    private static async Task<OsFixture> StartAsync(bool workers)
    {
        OsFixture os = await OsFixture.CreateAsync(workers);
        if (workers)
        {
            os.Startup.MarkStarted();
        }

        return os;
    }

    /// <summary>
    /// Starts hackwebd like an administrator would, and waits until it listens on <paramref name="port"/>
    /// with its <paramref name="bindings"/> prefixes: it opens them one by one, and a request for a
    /// host not bound yet would go elsewhere (a web app of the fixture named <c>intranet.hack</c>).
    /// </summary>
    private static async Task StartHackwebdAsync(OsFixture os, int port, int bindings = 1)
    {
        ShellRun start = await os.RunAsync("systemctl start hackwebd").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(start.ExitCode == 0, start.Stderr);
        await OsFixture.WaitUntilAsync(
            () => os.Ports.Snapshot().Where(row => row.Port == port).Sum(row => row.Prefixes.Count) >= bindings,
            because: $"hackwebd listens on {port} with {bindings} prefixes");
    }

    /// <summary>Writes the site files in <c>~/www</c> and a user site file pointing at them.</summary>
    private static async Task WriteUserSiteAsync(OsFixture os, string name, string bindings, bool directoryBrowsing = false, bool enabled = true)
    {
        await WriteUserFilesAsync(os);
        await os.WriteFileAsync($"/home/alice/.config/hackweb/sites/{name}.site", SiteJson(name, "/home/alice/www", bindings, directoryBrowsing, enabled));
    }

    private static async Task WriteUserFilesAsync(OsFixture os)
    {
        await os.WriteFileAsync("/home/alice/www/index.html", Index);
        await os.WriteFileAsync("/home/alice/www/css/site.css", "h1 { color: lime; }\n");
    }

    private static string SiteJson(string name, string root, string bindings, bool directoryBrowsing = false, bool enabled = true) =>
        $$"""
        {
          "name": "{{name}}",
          "root": "{{root}}",
          "bindings": {{bindings}},
          "defaultDocuments": ["index.html"],
          "directoryBrowsing": {{(directoryBrowsing ? "true" : "false")}},
          "enabled": {{(enabled ? "true" : "false")}}
        }
        """;

    /// <summary>Runs a line until its output satisfies <paramref name="done"/> (hot reload and log flushes are asynchronous).</summary>
    private static async Task<ShellRun> WaitForAsync(OsFixture os, string line, Func<ShellRun, bool> done, bool flush = false)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            if (flush)
            {
                await os.Log.FlushAsync();
            }

            ShellRun run = await os.RunAsync(line).WaitAsync(TimeSpan.FromSeconds(20));
            if (done(run) || DateTime.UtcNow > deadline)
            {
                return run;
            }

            await Task.Delay(50);
        }
    }

    private static AppOperationContext Root() => new()
    {
        AppId = "org.hackeros.kernel",
        UserId = "system",
        UserAuthority = AppAuthority.System,
        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
        IsSystemOperation = true
    };

    private static async Task RootCreateDirectoryAsync(IFileSystemService fileSystem, string path)
    {
        FileSystemMutationResult created = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse(path), FileSystemEntryKind.Directory, FileSystemPermissions.FromMode(0b111_101_101)),
            new FileSystemAuthorizationContext(Root(), ["system"], DateTimeOffset.UtcNow));
        Assert.True(created.Succeeded, created.Transaction.Error?.Code.ToString());
    }

    /// <summary>Writes a file as root, then sets its mode, like <c>sudo tee</c> then <c>sudo chmod</c>.</summary>
    private static async Task RootWriteAsync(IFileSystemService fileSystem, string path, string content, ushort mode)
    {
        FileSystemAuthorizationContext root = new(Root(), ["system"], DateTimeOffset.UtcNow);
        FileSystemMutationResult created = await fileSystem.CreateAsync(
            new FileSystemCreateRequest(VirtualPath.Parse(path), FileSystemEntryKind.File, FileSystemPermissions.FromMode(mode)), root);
        Assert.True(created.Succeeded, created.Transaction.Error?.Code.ToString());
        FileSystemMutationResult write = await fileSystem.WriteAsync(new FileSystemWriteRequest(VirtualPath.Parse(path)), new TextSource(content), root);
        Assert.True(write.Succeeded, write.Transaction.Error?.Code.ToString());
    }

    private sealed class TextSource(string content) : IFileSystemContentSource
    {
        private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(content);

        public FileSystemContentDescriptor Descriptor { get; } = FileSystemContentDescriptor.Text();

        public long? Length => _bytes.LongLength;

        public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Stream>(new MemoryStream(_bytes, writable: false));
    }
}
