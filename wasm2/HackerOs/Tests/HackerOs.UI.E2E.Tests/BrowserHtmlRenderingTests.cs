using System.Diagnostics;
using HackerOs.Platform.Core.FileSystem;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// The Browser's real HTML rendering (ADR 0075, chantier <c>web/4-browser-html</c>) in a real
/// Chromium: the page runs in the isolated frame with its style sheet and script inlined, and its
/// <c>fetch</c>, cookies, <c>localStorage</c> and links go through the page bridge.
/// </summary>
public sealed class BrowserHtmlRenderingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Browser_renders_web_and_local_html_pages_in_the_isolated_frame()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: true, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
                new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await E2ESupport.SeedFixturesAsync(page, output);
            await E2ESupport.OpenAppAsync(page, "Browser", output);

            ILocator addressInput = page.Locator("#browser-address-input");
            await addressInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // --- A web page of the simulated network: CSS, script, same-origin fetch, cookie ---
            await addressInput.FillAsync("example.hackeros/page.html");
            await addressInput.PressAsync("Enter");

            IFrameLocator frame = page.FrameLocator(".page-frame");
            await Assertions.Expect(frame.Locator("#script-status")).ToHaveTextAsync("script ran", new() { Timeout = 20000 });
            await Assertions.Expect(frame.Locator("#fetch-status")).ToHaveTextAsync("pong", new() { Timeout = 20000 });
            await Assertions.Expect(frame.Locator("#cookie-status")).ToHaveTextAsync("visited=yes");
            await Assertions.Expect(frame.Locator("#title")).ToHaveCSSAsync("color", "rgb(255, 0, 0)");
            output.WriteLine("[test] web page rendered with its style sheet, script, fetch and cookie.");

            // The frame is sandboxed without allow-same-origin: the page cannot reach Kazo OS.
            string? sandbox = await page.Locator(".page-frame").GetAttributeAsync("sandbox");
            Assert.Equal("allow-scripts allow-forms", sandbox);

            // --- The Network panel saw the document and its sub-resources ---
            await page.Locator("#browser-btn-devtools").ClickAsync();
            await page.Locator("#devtools-tab-network").ClickAsync();
            ILocator network = page.Locator(".network-table");
            await Assertions.Expect(network).ToContainTextAsync("page.html");
            await Assertions.Expect(network).ToContainTextAsync("site.css");
            await Assertions.Expect(network).ToContainTextAsync("site.js");
            await Assertions.Expect(network).ToContainTextAsync("ping");

            // --- View source shows the HTML as received ---
            await page.Locator("#browser-btn-source").ClickAsync();
            await Assertions.Expect(page.Locator(".body-viewer-text")).ToContainTextAsync("<script src=\"/site.js\"></script>");
            await page.Locator("#browser-btn-source").ClickAsync();
            await Assertions.Expect(frame.Locator("#script-status")).ToHaveTextAsync("script ran", new() { Timeout = 20000 });

            // --- XMLHttpRequest and a form POST go through the bridge ---
            await addressInput.FillAsync("example.hackeros/form.html");
            await addressInput.PressAsync("Enter");
            await Assertions.Expect(frame.Locator("#xhr-status")).ToHaveTextAsync("200 xhr pong", new() { Timeout = 20000 });
            await frame.Locator("#send").ClickAsync();
            await Assertions.Expect(frame.Locator("#echo")).ToHaveTextAsync("msg=hello bridge", new() { Timeout = 20000 });
            await Assertions.Expect(addressInput).ToHaveValueAsync("https://example.hackeros/echo");

            // --- A local file: file:// with relative files, localStorage, a link, back ---
            string siteUrl = $"file://{E2ESupport.HomePath}/Documents/{TestDemoFixtureSeeder.SiteDirectoryName}/index.html";
            await addressInput.FillAsync(siteUrl);
            await addressInput.PressAsync("Enter");

            await Assertions.Expect(frame.Locator("#script-status")).ToHaveTextAsync("script ran", new() { Timeout = 20000 });
            await Assertions.Expect(frame.Locator("#storage-status")).ToHaveTextAsync("visits=1");
            await Assertions.Expect(frame.Locator("#fetch-status")).ToHaveTextAsync("pong", new() { Timeout = 20000 });
            await Assertions.Expect(frame.Locator("#title")).ToHaveCSSAsync("color", "rgb(255, 0, 0)");

            await page.Locator("#devtools-tab-console").ClickAsync();
            await Assertions.Expect(page.Locator(".devtools")).ToContainTextAsync("e2e-site-loaded");

            await frame.Locator("#about-link").ClickAsync();
            await Assertions.Expect(frame.Locator("#about")).ToHaveTextAsync("About page", new() { Timeout = 20000 });
            await Assertions.Expect(addressInput).ToHaveValueAsync(siteUrl.Replace("index.html", "about.html", StringComparison.Ordinal));

            await page.Locator("#browser-btn-back").ClickAsync();
            await Assertions.Expect(frame.Locator("#storage-status")).ToHaveTextAsync("visits=2", new() { Timeout = 20000 });
            output.WriteLine("[test] local site rendered; localStorage survived the navigation.");

            // --- A missing host shows the browser's error page ---
            await addressInput.FillAsync("https://nowhere.hack/");
            await addressInput.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".browser-error-page")).ToContainTextAsync("ERR_NAME_NOT_RESOLVED", new() { Timeout = 20000 });
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
