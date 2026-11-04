namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0084 §2 end to end: a site with a Blazor root, started on demand by <c>curl</c>, answers its
/// API, the host page for any page path, the boot manifest and the loader script, on the main
/// thread and in the web worker.
/// </summary>
public sealed class BlazorSiteTests
{
    public static TheoryData<bool> Modes => new() { false, true };

    [Theory]
    [MemberData(nameof(Modes))]
    public async Task Curl_gets_the_host_page_the_boot_manifest_and_the_api(bool workers)
    {
        await using OsFixture os = await StartAsync(workers);

        ShellRun home = await os.RunAsync("curl -i http://blazor.hack/").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun page = await os.RunAsync("curl http://blazor.hack/notes/5").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun boot = await os.RunAsync("curl http://blazor.hack/_framework/blazor.boot.json").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun script = await os.RunAsync("curl -i http://blazor.hack/_framework/blazor.hack.js").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun api = await os.RunAsync("curl http://blazor.hack/api/time").WaitAsync(TimeSpan.FromSeconds(20));
        ShellRun missing = await os.RunAsync("curl -i http://blazor.hack/css/site.css").WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(home.ExitCode == 0, home.Stderr);
        Assert.True(home.Stdout.StartsWith("HTTP/1.1 200", StringComparison.Ordinal), home.Stdout);
        Assert.True(home.Stdout.Contains("text/html", StringComparison.OrdinalIgnoreCase), home.Stdout);
        Assert.Contains("Content-Type: text/html", home.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("_framework/blazor.hack.js", home.Stdout, StringComparison.Ordinal);
        Assert.Contains("<div id=\"app\">", page.Stdout, StringComparison.Ordinal);
        Assert.Contains("\"appId\": \"org.test.blazor\"", boot.Stdout, StringComparison.Ordinal);
        Assert.Contains(typeof(BlazorSite).FullName!, boot.Stdout, StringComparison.Ordinal);
        Assert.Contains("Content-Type: text/javascript", script.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("now", api.Stdout.TrimEnd());
        Assert.StartsWith("HTTP/1.1 404", missing.Stdout, StringComparison.Ordinal);
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
}
