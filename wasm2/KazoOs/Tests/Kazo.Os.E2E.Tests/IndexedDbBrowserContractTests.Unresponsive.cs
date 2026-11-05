using System.Diagnostics;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Kazo.Os.E2E.Tests;

/// <summary>
/// ADR 0127 §2-3 in Chromium: a window whose app stops answering gets the "(Not responding)" suffix,
/// greyed content and the "Wait | End the app" alert dialog, attached to that window only.
/// </summary>
public sealed partial class IndexedDbBrowserContractTests
{
    private const string UnresponsiveWindowSelector = "article.window-host[data-window-id='40000000-0000-0000-0000-000000000001']";

    /// <summary>A hung app: suffix, grey, dialog with focus and Tab trap, Escape waits, the dialog comes back, X reopens it, End the app stops the process.</summary>
    [Fact]
    public async Task A_hung_app_window_shows_the_not_responding_dialog_and_can_be_ended()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=unresponsive");
            ILocator window = page.Locator(UnresponsiveWindowSelector);
            await window.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
            ILocator title = window.Locator("header.window-chrome h2");
            ILocator dialog = window.GetByRole(AriaRole.Alertdialog);
            await Assertions.Expect(title).ToHaveTextAsync("Calculatrice");

            await page.Locator("#hang-app").ClickAsync();

            // The suffix is display only; the dialog belongs to the window and takes the keyboard.
            await Assertions.Expect(title).ToHaveTextAsync("Calculatrice (Not responding)", new() { Timeout = 10000 });
            await Assertions.Expect(window).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("is-not-responding"));
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            await Assertions.Expect(dialog).ToContainTextAsync("“Calculatrice” is not responding.");
            ILocator wait = dialog.GetByRole(AriaRole.Button, new() { Name = "Wait" });
            ILocator terminate = dialog.GetByRole(AriaRole.Button, new() { Name = "End the app" });
            await Assertions.Expect(wait).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(terminate).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(wait).ToBeFocusedAsync();

            var axe = await page.RunAxe();
            string[] blocking = [.. (axe.Violations ?? [])
                .Where(item => item.Impact is "serious" or "critical")
                .Select(item => item.Id)];
            Assert.True(blocking.Length == 0, $"serious/critical axe violations with the dialog open: {string.Join(", ", blocking)}");

            // Escape is "Wait": the dialog closes, the window stays marked, and the dialog comes back.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(dialog).ToBeHiddenAsync();
            await Assertions.Expect(title).ToHaveTextAsync("Calculatrice (Not responding)");
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 10000 });

            // Wait, then the window's X: the close button opens the same dialog instead of closing.
            await wait.ClickAsync();
            await Assertions.Expect(dialog).ToBeHiddenAsync();
            await window.GetByLabel("Close").ClickAsync();
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 5000 });
            await Assertions.Expect(window).ToBeVisibleAsync();

            await terminate.ClickAsync();
            await Assertions.Expect(page.Locator("#process-state")).ToHaveTextAsync("terminated:Terminated", new() { Timeout = 10000 });
            await Assertions.Expect(window).ToHaveCountAsync(0);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>A recovered app clears the state; a real main-thread freeze with a click queued during it shows the dialog afterwards (case 3).</summary>
    [Fact]
    public async Task A_click_given_during_a_real_freeze_shows_the_dialog_once_the_thread_is_back()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=unresponsive");
            ILocator window = page.Locator(UnresponsiveWindowSelector);
            await window.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
            ILocator title = window.Locator("header.window-chrome h2");
            ILocator dialog = window.GetByRole(AriaRole.Alertdialog);

            // A hang that ends on its own: the state ends with the process's next short slice.
            await page.Locator("#hang-app").ClickAsync();
            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 10000 });
            await page.Locator("#recover-app").ClickAsync();
            await Assertions.Expect(title).ToHaveTextAsync("Calculatrice", new() { Timeout = 10000 });
            await Assertions.Expect(dialog).ToHaveCountAsync(0);

            // Case 3: the app holds the main thread for 1.5 s; a click queued meanwhile waits for it.
            LocatorBoundingBoxResult box = await window.Locator("#app-button").BoundingBoxAsync()
                ?? throw new InvalidOperationException("The app button has no box.");
            await page.EvaluateAsync("() => setTimeout(() => document.getElementById('freeze-app').click(), 50)");
            await Task.Delay(400);
            await page.Mouse.ClickAsync(box.X + (box.Width / 2), box.Y + (box.Height / 2));

            await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(title).ToHaveTextAsync("Calculatrice (Not responding)");

            // The app is idle again: after one threshold without work it answers and the state ends.
            await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = 10000 });
            await Assertions.Expect(title).ToHaveTextAsync("Calculatrice");
        }
        finally
        {
            StopProcess(server);
        }
    }
}
