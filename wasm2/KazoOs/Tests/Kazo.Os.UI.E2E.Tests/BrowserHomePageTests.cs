using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// The Browser's home page in a real Chromium, outside test-demo mode: opening the Browser lands
/// on KazoSearch (<c>https://hackersearch.net</c>), a host of the simulated network, and its
/// search form returns results — never the <c>ERR_NAME_NOT_RESOLVED</c> error page.
/// </summary>
public sealed class BrowserHomePageTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Browser_opens_on_the_KazoSearch_home_page_and_searches()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

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

            // --- The home page loads at once, with no address typed ---
            await Assertions.Expect(page.Locator(".sim-page-title")).ToHaveTextAsync("KazoSearch", new() { Timeout = 20000 });
            await Assertions.Expect(page.Locator(".browser-error-page")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("#browser-address-input")).ToHaveValueAsync("https://hackersearch.net/");
            output.WriteLine("[test] KazoSearch home page rendered.");

            // --- A search typed in the address bar goes to KazoSearch and shows its results ---
            ILocator addressInput = page.Locator("#browser-address-input");
            await addressInput.FillAsync("exploit");
            await addressInput.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".sim-page-title")).ToHaveTextAsync("exploit - KazoSearch", new() { Timeout = 20000 });
            await Assertions.Expect(page.Locator(".browser-error-page")).ToHaveCountAsync(0);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
