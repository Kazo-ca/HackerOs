using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0095 §3: the fallback window keys work in an ordinary browser tab, without full screen or
/// keyboard lock — Alt+` switches windows through the switcher and Alt+Q closes the focused one.
/// </summary>
public sealed class WindowKeyboardCommandsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Alt_backquote_switches_windows_and_alt_q_closes_the_focused_one()
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
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            ILocator windows = page.Locator("article.window-host");
            ILocator focused = page.Locator("article.window-host.is-focused");

            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await Assertions.Expect(windows).ToHaveCountAsync(1, new() { Timeout = 15000 });
            string first = (await focused.GetAttributeAsync("data-window-id"))!;

            await E2ESupport.OpenAppAsync(page, "File Explorer", output);
            await Assertions.Expect(windows).ToHaveCountAsync(2, new() { Timeout = 15000 });
            await Assertions.Expect(focused).Not.ToHaveAttributeAsync("data-window-id", first);
            string second = (await focused.GetAttributeAsync("data-window-id"))!;

            // Alt held, ` pressed: the switcher opens on the previous window; releasing Alt commits.
            await page.Keyboard.DownAsync("Alt");
            await page.Keyboard.PressAsync("Backquote");
            ILocator switcher = page.Locator(".window-switcher");
            await Assertions.Expect(switcher).ToBeVisibleAsync();
            await Assertions.Expect(switcher.Locator(".window-switcher-item.is-selected"))
                .ToHaveAttributeAsync("data-window-switcher-item", first);
            await page.Keyboard.UpAsync("Alt");

            await Assertions.Expect(switcher).ToBeHiddenAsync();
            await Assertions.Expect(focused).ToHaveAttributeAsync("data-window-id", first);
            output.WriteLine("[test] Alt+` brought the first window back to the front.");

            // Alt+Q closes the focused window and the other one takes the focus.
            await page.Keyboard.PressAsync("Alt+KeyQ");
            await Assertions.Expect(windows).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(focused).ToHaveAttributeAsync("data-window-id", second);
            output.WriteLine("[test] Alt+Q closed the focused window.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
