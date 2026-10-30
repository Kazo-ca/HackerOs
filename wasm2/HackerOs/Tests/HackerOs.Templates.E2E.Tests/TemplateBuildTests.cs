using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Playwright;

namespace HackerOs.Templates.E2E.Tests;

/// <summary>ADR 0098 §6: each template, generated with <c>dotnet new</c> and built from the local feed.</summary>
[Collection(TemplateWorkspaceCollection.Name)]
public sealed class TemplateBuildTests(TemplateWorkspace workspace)
{
    /// <summary>An app template builds and its manifest names the app id and the SDK it was built against.</summary>
    [Theory]
    [InlineData("hackeros-window", "window", "Contoso.WindowApp")]
    [InlineData("hackeros-command", "terminal", "Contoso.CommandApp")]
    [InlineData("hackeros-service", "service", "Contoso.ServiceApp")]
    [InlineData("hackeros-site", "web", "Contoso.SiteApp")]
    public async Task An_app_template_builds_against_the_local_feed(string template, string kind, string name)
    {
        string appId = "com.contoso." + template["hackeros-".Length..];
        string directory = Path.Combine(workspace.Root, name);

        await workspace.RunCheckedAsync(workspace.Root, "new", template, "-n", name, "-o", directory, "--appId", appId);
        await workspace.RunCheckedAsync(directory, "build");

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        Assert.Equal(appId, manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal(kind, manifest.RootElement.GetProperty("kind").GetString());
        Assert.Equal(workspace.Version, manifest.RootElement.GetProperty("sdkCompatibility").GetProperty("minimumVersion").GetString());
    }

    /// <summary>
    /// ADR 0176 §8, ADR 0207: a repository made with <c>hackeros-solution</c>, its HackerOS submodule
    /// replaced by this repository (<c>HackerOsSourceRoot</c>), builds and tests both profiles. Debug
    /// compiles HackerOS from the sources (every HackerOS reference is a project, the host aggregates
    /// the app with the base apps, system apps included); Release takes the packages of the local
    /// feed. Without the submodule the build stops with HOSAPP0070.
    /// </summary>
    [Fact]
    public async Task The_solution_template_builds_and_tests_both_profiles()
    {
        string directory = Path.Combine(workspace.Root, "Contoso.Board");
        string sourceRoot = "-p:HackerOsSourceRoot=" + workspace.RepositoryRoot + Path.DirectorySeparatorChar;
        await workspace.RunCheckedAsync(workspace.Root, "new", "hackeros-solution", "-n", "Contoso.Board", "-o", directory,
            "--appId", "com.contoso.board", "--repository", "contoso/HackerOs-App-Board");

        Assert.True(File.Exists(Path.Combine(directory, "Contoso.Board.sln")));
        Assert.Contains("external/HackerOs", File.ReadAllText(Path.Combine(directory, ".gitmodules")), StringComparison.Ordinal);
        Assert.Contains("contoso/HackerOs-App-Board", File.ReadAllText(Path.Combine(directory, "store", "store.json")), StringComparison.Ordinal);
        Assert.Contains("src/Contoso.Board", File.ReadAllText(Path.Combine(directory, ".github", "workflows", "publish-to-hack-store.yml")), StringComparison.Ordinal);

        (int missingExit, string missingOutput) = await workspace.RunAsync(directory, "build", "-c", "Release");
        Assert.NotEqual(0, missingExit);
        Assert.Contains("HOSAPP0070", missingOutput, StringComparison.Ordinal);

        // Debug: the sources of the submodule.
        await workspace.RunCheckedAsync(directory, "build", "-c", "Debug", sourceRoot);
        await workspace.RunCheckedAsync(directory, "test", "-c", "Debug", "--no-build", sourceRoot);
        string host = Path.Combine(directory, "src", "Contoso.Board.Os");
        AssertHackerOsReferences(Path.Combine(host, "obj", "source", "project.assets.json"), "project");
        string debugCatalog = File.ReadAllText(Path.Combine(host, "obj", "source", "Debug", "net10.0", "BuiltInApplications.json"));
        Assert.Contains("\"com.contoso.board\"", debugCatalog, StringComparison.Ordinal);
        Assert.Contains("\"org.hackeros.terminal\"", debugCatalog, StringComparison.Ordinal);
        Assert.Contains("\"isSystemApp\":true", debugCatalog, StringComparison.Ordinal);
        using (JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "src", "Contoso.Board", "manifest.json"))))
        {
            Assert.Equal("com.contoso.board", manifest.RootElement.GetProperty("id").GetString());
            Assert.Equal(workspace.Version, manifest.RootElement.GetProperty("sdkCompatibility").GetProperty("minimumVersion").GetString());
        }

        // Release: the packages, at the submodule's version.
        await workspace.RunCheckedAsync(directory, "build", "-c", "Release", sourceRoot);
        await workspace.RunCheckedAsync(directory, "test", "-c", "Release", "--no-build", sourceRoot);
        AssertHackerOsReferences(Path.Combine(host, "obj", "project.assets.json"), "package");
        string releaseCatalog = File.ReadAllText(Path.Combine(host, "obj", "Release", "net10.0", "BuiltInApplications.json"));
        Assert.Contains("\"com.contoso.board\"", releaseCatalog, StringComparison.Ordinal);

        // The publishing workflow packs the app the same way (ADR 0188 §7, ADR 0182).
        await workspace.RunCheckedAsync(directory, "build", Path.Combine("src", "Contoso.Board"), "-c", "Release", "-t:HackerOsPack", sourceRoot);
        Assert.True(File.Exists(Path.Combine(directory, "src", "Contoso.Board", "bin", "hackeros-packages", "index.json")));
    }

    /// <summary>Every HackerOS library of a restore output has the expected type (<c>project</c> or <c>package</c>).</summary>
    private static void AssertHackerOsReferences(string assetsFile, string expectedType)
    {
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
        JsonProperty[] hackerOs = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Name.StartsWith("HackerOs.", StringComparison.Ordinal))
            .ToArray();
        Assert.Contains(hackerOs, library => library.Name.StartsWith("HackerOs.AppSdk/", StringComparison.Ordinal));
        Assert.Contains(hackerOs, library => library.Name.StartsWith("HackerOs.Ecosystem.Host/", StringComparison.Ordinal));
        Assert.All(hackerOs, library => Assert.Equal(expectedType, library.Value.GetProperty("type").GetString()));
    }

    /// <summary>
    /// The host template, with a generated window app and command added as project references, builds,
    /// aggregates them with the base app packages, boots in Chromium and runs both.
    /// </summary>
    [Fact]
    public async Task The_host_template_boots_and_runs_generated_apps()
    {
        string root = Path.Combine(workspace.Root, "host-scenario");
        Directory.CreateDirectory(root);
        await workspace.RunCheckedAsync(root, "new", "hackeros-window", "-n", "Contoso.Notes", "-o", "Notes", "--appId", "com.contoso.notes");
        await workspace.RunCheckedAsync(root, "new", "hackeros-command", "-n", "Contoso.Hello", "-o", "Hello", "--appId", "com.contoso.hello");
        await workspace.RunCheckedAsync(root, "new", "hackeros-host", "-n", "Contoso.Os", "-o", "Host");
        string host = Path.Combine(root, "Host");
        await workspace.RunCheckedAsync(host, "add", "reference", "../Notes/Contoso.Notes.csproj", "../Hello/Contoso.Hello.csproj");
        await workspace.RunCheckedAsync(host, "build");

        string aggregated = File.ReadAllText(Path.Combine(host, "obj", "Debug", "net10.0", "BuiltInApplications.json"));
        Assert.Contains("\"com.contoso.notes\"", aggregated, StringComparison.Ordinal);
        Assert.Contains("\"com.contoso.hello\"", aggregated, StringComparison.Ordinal);
        Assert.Contains("\"org.hackeros.terminal\"", aggregated, StringComparison.Ordinal);

        int port = FreePort();
        using System.Diagnostics.Process server = workspace.Start(host, "run", "--no-build", "--urls", $"http://127.0.0.1:{port}");
        try
        {
            await WaitForAsync($"http://127.0.0.1:{port}/");

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions());
            IPage page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 800 } });
            await page.GotoAsync($"http://127.0.0.1:{port}/");

            // First boot: create the administrator, then the desktop.
            await page.Locator("input").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 120_000 });
            ILocator inputs = page.Locator("input");
            await inputs.Nth(0).FillAsync("neo");
            await inputs.Nth(1).FillAsync("Neo");
            await inputs.Nth(2).FillAsync("matrix123");
            await inputs.Nth(3).FillAsync("matrix123");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create local profile" }).ClickAsync();

            // The generated window app, lazy-loaded from its project reference.
            await page.GetByText("Applications", new PageGetByTextOptions { Exact = true }).ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
            await page.GetByText("Contoso.Notes", new PageGetByTextOptions { Exact = true }).First.ClickAsync();
            await page.GetByText("Clicked 0 times").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.GetByText("Clicked 0 times").ClickAsync();
            await page.GetByText("Clicked 1 times").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

            // The generated command, run from the Terminal package.
            await page.GetByText("Applications", new PageGetByTextOptions { Exact = true }).ClickAsync();
            await page.GetByText("Terminal", new PageGetByTextOptions { Exact = true }).First.ClickAsync();
            await page.GetByText("Type 'help'").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.Keyboard.TypeAsync("hello Morpheus");
            await page.Keyboard.PressAsync("Enter");
            await page.GetByText("Hello, Morpheus!").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        finally
        {
            server.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// ADR 0098 §3 and §8, ADR 0114, ADR 0149 §5, ADR 0161 §3: the app packages of
    /// <c>Tests/fixtures/sdk-compat</c> in a host made by the template. A host that references the
    /// apps compiled against SDK 1.0.0 and 1.1.0 does not build (<c>HOSAPP0021</c>, another major:
    /// they must be rebuilt). With the app compiled against SDK 2.0.0 instead, the host boots, the
    /// window shows the OS file control in a third-party app and the command copies a file with the
    /// SDK 2.0 copy rules.
    /// </summary>
    [Fact]
    public async Task A_host_runs_SDK_2_app_packages_and_refuses_SDK_1_x_packages()
    {
        string root = Path.Combine(workspace.Root, "sdk-compat-scenario");
        Directory.CreateDirectory(root);
        await workspace.RunCheckedAsync(root, "new", "hackeros-host", "-n", "Contoso.Os", "-o", "Host");
        string host = Path.Combine(root, "Host");

        await workspace.RunCheckedAsync(host, "add", "package", "Contoso.Sdk1Compat", "--version", "1.0.0");
        await workspace.RunCheckedAsync(host, "add", "package", "Contoso.Sdk11Compat", "--version", "1.1.0");
        (int exitCode, string output) = await workspace.RunAsync(host, "build");
        Assert.NotEqual(0, exitCode);
        foreach (string appId in new[] { "com.contoso.sdk1-window", "com.contoso.sdk1-command", "com.contoso.sdk11-window", "com.contoso.sdk11-command" })
        {
            Assert.Contains($"error HOSAPP0021: [{appId}]", output, StringComparison.Ordinal);
        }

        await workspace.RunCheckedAsync(host, "remove", "package", "Contoso.Sdk1Compat");
        await workspace.RunCheckedAsync(host, "remove", "package", "Contoso.Sdk11Compat");
        await workspace.RunCheckedAsync(host, "add", "package", "Contoso.Sdk2Compat", "--version", "2.0.0");
        await workspace.RunCheckedAsync(host, "build");

        string aggregated = File.ReadAllText(Path.Combine(host, "obj", "Debug", "net10.0", "BuiltInApplications.json"));
        Assert.Contains("\"com.contoso.sdk2-window\"", aggregated, StringComparison.Ordinal);
        Assert.Contains("\"com.contoso.sdk2-command\"", aggregated, StringComparison.Ordinal);
        Assert.DoesNotContain("\"com.contoso.sdk1-window\"", aggregated, StringComparison.Ordinal);

        int port = FreePort();
        using System.Diagnostics.Process server = workspace.Start(host, "run", "--no-build", "--urls", $"http://127.0.0.1:{port}");
        try
        {
            await WaitForAsync($"http://127.0.0.1:{port}/");

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions());
            IPage page = await browser.NewPageAsync(new BrowserNewPageOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 800 } });
            await page.GotoAsync($"http://127.0.0.1:{port}/");

            await page.Locator("input").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 120_000 });
            ILocator inputs = page.Locator("input");
            await inputs.Nth(0).FillAsync("neo");
            await inputs.Nth(1).FillAsync("Neo");
            await inputs.Nth(2).FillAsync("matrix123");
            await inputs.Nth(3).FillAsync("matrix123");
            await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Create local profile" }).ClickAsync();

            // The window app of SDK 2.0.0, lazy-loaded from its package, shows the OS file control
            // (HackFileView, provided by the File Explorer).
            await page.GetByText("Applications", new PageGetByTextOptions { Exact = true }).ClickAsync(new LocatorClickOptions { Timeout = 60_000 });
            await page.GetByText("SDK 2.0 Window", new PageGetByTextOptions { Exact = true }).First.ClickAsync();
            await page.GetByText("Compiled against SDK 2.0.0").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.Locator(".compat-files table.file-view-details").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

            // Its command, run from the Terminal, copies a file with HackFileOperations.
            await page.GetByText("Applications", new PageGetByTextOptions { Exact = true }).ClickAsync();
            await page.GetByText("Terminal", new PageGetByTextOptions { Exact = true }).First.ClickAsync();
            await page.GetByText("Type 'help'").WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            await page.Keyboard.TypeAsync("sdk2-compat still binds");
            await page.Keyboard.PressAsync("Enter");
            await page.GetByText($"sdk2-compat: still binds [sdk2-compat (copy).txt] (compiled against 2.0.0, running on {workspace.Version})")
                .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        }
        finally
        {
            server.Kill(entireProcessTree: true);
        }
    }

    private static int FreePort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForAsync(string url)
    {
        using HttpClient http = new();
        for (int attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"{url} did not answer.");
    }
}
