using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// The Intranet sample (chantier <c>web/11-sample-final</c>) in a real Chromium: every brick of the web
/// phase in one walk. MVC pages (<c>.cshtml</c> and <c>.razor</c> views) render in the browser's isolated
/// page with their CSS and scripts; a script filters the directory through the REST API; a form with an
/// anti-forgery token puts a name in the cookie session; the Blazor board under <c>/board</c> is drawn in
/// the page area, sees the same session, adds a card through the API and navigates within itself; a
/// <c>target="_top"</c> link brings the MVC pages back.
/// </summary>
public sealed class BrowserIntranetTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Browser_walks_the_intranet_pages_api_session_and_blazor_board()
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
            await E2ESupport.OpenAppAsync(page, "Browser", output);

            ILocator addressInput = page.Locator("#browser-address-input");
            await addressInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // --- The first request starts the site; its home page is a .cshtml view in the isolated page ---
            await addressInput.FillAsync("http://intranet.hack/");
            await addressInput.PressAsync("Enter");
            IFrameLocator frame = page.FrameLocator(".page-frame");
            await Assertions.Expect(frame.Locator("#welcome")).ToHaveTextAsync("Welcome, stranger", new() { Timeout = 30000 });
            // health.js asked /health with fetch through the browser's bridge.
            await Assertions.Expect(frame.Locator("#site-health")).ToHaveTextAsync("up", new() { Timeout = 20000 });
            await Assertions.Expect(page.Locator(".browser-statusbar")).ToContainTextAsync("Home - Intranet");
            output.WriteLine("[test] the home page rendered and its script reached /health.");

            // --- The directory: people.js filters through GET /api/people as the user types ---
            await frame.Locator("#nav-people").ClickAsync();
            await Assertions.Expect(frame.Locator("#people-count")).ToHaveTextAsync("8 result(s)", new() { Timeout = 20000 });
            await frame.Locator("#people-query").PressSequentiallyAsync("web");
            await Assertions.Expect(frame.Locator("#people-count")).ToHaveTextAsync("2 result(s)", new() { Timeout = 20000 });
            await Assertions.Expect(frame.Locator("#people-rows")).ToContainTextAsync("Morpheus");
            output.WriteLine("[test] the directory script filtered through the API.");

            // --- A form with its anti-forgery token: the name goes into the cookie session ---
            await frame.Locator("#nav-profile").ClickAsync();
            await frame.Locator("#Name").FillAsync("Neo");
            await frame.Locator("#profile-save").ClickAsync();
            await Assertions.Expect(frame.Locator("#welcome")).ToHaveTextAsync("Welcome back, Neo", new() { Timeout = 30000 });
            await Assertions.Expect(frame.Locator("#whoami")).ToHaveTextAsync("Neo");
            await Assertions.Expect(addressInput).ToHaveValueAsync("http://intranet.hack/");
            output.WriteLine("[test] the profile form posted, redirected and kept the session.");

            // --- The Blazor board: drawn in the page area, same origin, same cookies ---
            await frame.Locator("#nav-board").ClickAsync();
            ILocator site = page.Locator(".site-view");
            await Assertions.Expect(site.Locator("#column-todo .board-card")).ToHaveCountAsync(2, new() { Timeout = 30000 });
            Assert.Equal(0, await page.Locator(".page-frame").CountAsync());
            await Assertions.Expect(site.Locator("#board-who")).ToHaveTextAsync("Neo", new() { Timeout = 20000 });
            await Assertions.Expect(addressInput).ToHaveValueAsync("http://intranet.hack/board");

            await site.Locator("#board-new-title").FillAsync("Walk the intranet in Chromium");
            await site.Locator("#board-add").ClickAsync();
            await Assertions.Expect(site.Locator("#column-todo .board-card")).ToHaveCountAsync(3, new() { Timeout = 20000 });
            await Assertions.Expect(site.Locator("#task-5")).ToContainTextAsync("@Neo");
            await site.Locator("#task-5 button[title='Move right']").ClickAsync();
            await Assertions.Expect(site.Locator("#column-doing #task-5")).ToBeVisibleAsync(new() { Timeout = 20000 });
            output.WriteLine("[test] the board added and moved a card through the API.");

            // --- A link within the board: the address changes, no new document ---
            await site.Locator("#task-5 .board-card-title").ClickAsync();
            await Assertions.Expect(addressInput).ToHaveValueAsync("http://intranet.hack/board/tasks/5");
            await Assertions.Expect(site.Locator("#task-title")).ToHaveValueAsync("Walk the intranet in Chromium", new() { Timeout = 20000 });
            await site.Locator("#task-back").ClickAsync();
            await Assertions.Expect(site.Locator("#column-doing #task-5")).ToBeVisibleAsync(new() { Timeout = 20000 });

            // --- target="_top" leaves the Blazor part: the MVC page is a new document again ---
            await site.Locator("#board-nav-notes").ClickAsync();
            await Assertions.Expect(addressInput).ToHaveValueAsync(new Regex("^http://intranet\\.hack/Notes$"), new() { Timeout = 30000 });
            await Assertions.Expect(page.FrameLocator(".page-frame").Locator("#notes-list")).ToContainTextAsync("Welcome to the intranet", new() { Timeout = 30000 });
            Assert.Equal(0, await page.Locator(".site-view").CountAsync());
            output.WriteLine("[test] the target=_top link went back to the MVC pages.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
