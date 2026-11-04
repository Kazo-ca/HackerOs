using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// A Blazor site drawn by the Browser (ADR 0084, chantier <c>web/9-blazor-sites</c>) in a real
/// Chromium: the sample site at <c>blazor-sample.hack</c> renders in the page area (no iframe), its
/// links change the address without a reload (the counter survives), a page calls the site's API,
/// back and forward stay in the site, and a crash of the site shows "Aw, Snap!" without touching
/// Kazo OS.
/// </summary>
public sealed class BrowserBlazorSiteTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Browser_draws_the_sample_blazor_site_and_navigates_within_it()
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

            // --- The host page starts the site's renderer: the root component is drawn in the page area ---
            await addressInput.FillAsync("http://blazor-sample.hack/");
            await addressInput.PressAsync("Enter");

            ILocator site = page.Locator(".site-view");
            await Assertions.Expect(site.Locator("#counter-value")).ToHaveTextAsync("0", new() { Timeout = 30000 });
            Assert.Equal(0, await page.Locator(".page-frame").CountAsync());
            await Assertions.Expect(page.Locator(".browser-statusbar")).ToContainTextAsync("Home - Blazor Sample Site");
            output.WriteLine("[test] the Blazor site rendered in the page area with its title.");

            await site.Locator("#counter-button").ClickAsync();
            await site.Locator("#counter-button").ClickAsync();
            await Assertions.Expect(site.Locator("#counter-value")).ToHaveTextAsync("2");

            // --- A site link changes the address without a request: the page calls the API ---
            await site.Locator("#link-hello").ClickAsync();
            await Assertions.Expect(site.Locator("#greeting")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^Hello, World!"), new() { Timeout = 30000 });
            await Assertions.Expect(addressInput).ToHaveValueAsync("http://blazor-sample.hack/hello/World");
            await Assertions.Expect(page.Locator(".browser-statusbar")).ToContainTextAsync("Hello World - Blazor Sample Site");
            // HackNavLink marks the active page: the home link (Match=All) is no longer active, the
            // menu's Hello link (/hello/Kazo OS) becomes active once followed with its own parameter.
            await Assertions.Expect(site.Locator("#nav-home")).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
            await site.Locator("#nav-hello").ClickAsync();
            await Assertions.Expect(site.Locator("#greeting")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^Hello, Kazo OS!"), new() { Timeout = 30000 });
            await Assertions.Expect(addressInput).ToHaveValueAsync("http://blazor-sample.hack/hello/Kazo OS");
            await Assertions.Expect(site.Locator("#nav-hello")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
            output.WriteLine("[test] link navigation stayed in the site and the API answered.");

            // --- Back stays in the site: the counter kept its value (no reload) ---
            await page.Locator("#browser-btn-back").ClickAsync();
            await Assertions.Expect(site.Locator("#greeting")).ToContainTextAsync("Hello, World!");
            await Assertions.Expect(addressInput).ToHaveValueAsync("http://blazor-sample.hack/hello/World");
            await page.Locator("#browser-btn-back").ClickAsync();
            await Assertions.Expect(site.Locator("#counter-value")).ToHaveTextAsync("2");
            await Assertions.Expect(addressInput).ToHaveValueAsync("http://blazor-sample.hack/");
            await page.Locator("#browser-btn-forward").ClickAsync();
            await Assertions.Expect(site.Locator("#greeting")).ToContainTextAsync("Hello, World!");
            output.WriteLine("[test] back and forward stayed within the site.");

            // --- A crash of the site's code: "Aw, Snap!", the OS and the browser survive ---
            await site.Locator("#nav-crash").ClickAsync();
            await site.Locator("#crash-button").ClickAsync();
            ILocator error = page.Locator(".browser-error-page");
            await Assertions.Expect(error).ToContainTextAsync("Aw, Snap!", new() { Timeout = 20000 });
            await Assertions.Expect(error).ToHaveAttributeAsync("data-error-code", "STATUS_ACCESS_VIOLATION");
            output.WriteLine("[test] the crash page appeared.");

            // --- Reload starts a new renderer: the site is back, counter reset ---
            await page.Locator("#browser-btn-refresh").ClickAsync();
            await Assertions.Expect(page.Locator(".site-view #crash-button")).ToBeVisibleAsync(new() { Timeout = 30000 });

            // --- A link out of the site is a browser navigation ---
            await page.Locator(".site-view #nav-external").ClickAsync();
            await Assertions.Expect(addressInput).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("hackersearch.net"), new() { Timeout = 30000 });
            Assert.Equal(0, await page.Locator(".site-view").CountAsync());
            output.WriteLine("[test] leaving the site ended its renderer.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
