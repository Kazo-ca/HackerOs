using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0220, end to end: the mouse's Back and Forward buttons, Alt+←/→ and the tab's own Back go back and
/// forward inside the File Explorer instead of moving the host tab, and HackerOS is never left.
/// </summary>
public sealed class HardwareNavigationTests(ITestOutputHelper output)
{
    /// <summary>Presses and releases one mouse button over the file list (Playwright has no buttons 4 and 5).</summary>
    private const string PressMouseButtonScript = """
        button => {
            const target = document.querySelector('.explorer-files') ?? document.body;
            for (const type of ['mousedown', 'mouseup']) {
                target.dispatchEvent(new MouseEvent(type, { bubbles: true, cancelable: true, button, view: window }));
            }
        }
        """;

    [Fact]
    public async Task Mouse_buttons_keyboard_and_the_tabs_Back_navigate_the_file_explorer()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            string osUrl = page.Url;
            await E2ESupport.OpenAppAsync(page, "File Explorer", output);

            ILocator addressInput = page.GetByLabel("Current path address");
            string documents = $"{E2ESupport.HomePath}/Documents";
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 15000 });
            ILocator documentsRow = page.Locator("tr.entry-row", new() { HasText = "Documents" });
            await documentsRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await documentsRow.DblClickAsync();
            await Assertions.Expect(addressInput).ToHaveValueAsync(documents, new() { Timeout = 15000 });

            // Mouse button 4 (Back), then 5 (Forward).
            await page.EvaluateAsync(PressMouseButtonScript, 3);
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 10000 });
            output.WriteLine("[test] mouse Back went back to home.");
            await page.EvaluateAsync(PressMouseButtonScript, 4);
            await Assertions.Expect(addressInput).ToHaveValueAsync(documents, new() { Timeout = 10000 });
            output.WriteLine("[test] mouse Forward went forward to Documents.");

            // Alt+← from the file list (not from the address field, which keeps its keys).
            await page.Locator(".explorer-files").ClickAsync(new() { Position = new() { X = 5, Y = 5 } });
            await page.Keyboard.PressAsync("Alt+ArrowLeft");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 10000 });
            await page.Keyboard.PressAsync("Alt+ArrowRight");
            await Assertions.Expect(addressInput).ToHaveValueAsync(documents, new() { Timeout = 10000 });
            output.WriteLine("[test] Alt+arrows went back and forward.");

            // The tab's own Back (what Android's Back button and the iOS swipe do) stays in HackerOS.
            int historyLength = await page.EvaluateAsync<int>("() => history.length");
            Assert.True(historyLength >= 2, $"the sentinel entry was not pushed (history.length = {historyLength})");
            await page.EvaluateAsync("() => history.back()");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 10000 });
            output.WriteLine("[test] the tab's Back went back inside the explorer.");

            // Nothing left to go back to: the gesture does nothing and HackerOS stays open.
            await page.WaitForTimeoutAsync(600);
            await page.EvaluateAsync("() => history.back()");
            await page.WaitForTimeoutAsync(1000);
            Assert.Equal(osUrl, page.Url);
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath);
            output.WriteLine("[test] with nothing to go back to, HackerOS stayed open.");

            // Mobile shell: the tab's Back (Android's Back button, the iOS swipe) runs the ◁ sequence, which
            // asks the app first.
            await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
            ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await panel.GetByRole(AriaRole.Radio, new() { Name = "Mobile" }).ClickAsync();
            ILocator home = page.Locator(".mobile-home-screen");
            await home.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await home.Locator(".mobile-home-grid").GetByRole(AriaRole.Button, new() { Name = "File Explorer", Exact = true }).ClickAsync();
            ILocator title = page.Locator(".mobile-explorer .appbar-title");
            await Assertions.Expect(title).ToHaveTextAsync(E2ESupport.LoginName, new() { Timeout = 15000 });
            await page.Locator(".mobile-explorer .entry-row, .mobile-explorer .file-view-tile", new() { HasText = "Documents" }).First.ClickAsync();
            await Assertions.Expect(title).ToHaveTextAsync("Documents", new() { Timeout = 15000 });
            await page.WaitForTimeoutAsync(600);
            await page.EvaluateAsync("() => history.back()");
            await Assertions.Expect(title).ToHaveTextAsync(E2ESupport.LoginName, new() { Timeout = 10000 });
            Assert.Equal(osUrl, page.Url);
            output.WriteLine("[test] on the mobile shell, the tab's Back went back inside the explorer.");

            // Next Back: the explorer has nothing left, so the ◁ sequence closes it and shows Home.
            await page.WaitForTimeoutAsync(600);
            await page.EvaluateAsync("() => history.back()");
            await home.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            Assert.Equal(osUrl, page.Url);
            output.WriteLine("[test] then Back closed the explorer and showed Home, still in HackerOS.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
