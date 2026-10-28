using Microsoft.Playwright;

namespace HackerOs.E2E.Tests;

/// <summary>Window portal between screens (ADR 0178 §3, doc/en/wasm/dev/window-portal.md).</summary>
public sealed partial class IndexedDbBrowserContractTests
{
    /// <summary>
    /// Verifies that a window changing screen moves into the other screen's window layer without
    /// recreating its components, keeps its state and keyboard focus, and comes back to screen 1
    /// when screen 2's area goes away.
    /// </summary>
    [Fact]
    public async Task Window_moves_between_screen_layers_without_being_recreated()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using System.Diagnostics.Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(
                new BrowserNewContextOptions { ViewportSize = new() { Width = 1100, Height = 600 } });
            IPage page = await context.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            page.PageError += (_, error) => failures.Add($"page: {error}");
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=screen-portal");
            await page.Locator(".screen-portal-scenario").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            const string WindowOnScreen = "n => !!document.querySelector(`.screen-portal-screen[data-screen='${n}'] .window-layer .portal-probe`)";
            ILocator probe = page.Locator(".portal-probe");
            await probe.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.WaitForFunctionAsync(WindowOnScreen, 1);
            string? instance = await probe.GetAttributeAsync("data-instance");

            await probe.Locator(".portal-increment").ClickAsync();
            await probe.Locator(".portal-increment").ClickAsync();
            await probe.Locator(".portal-input").FillAsync("kept");
            await probe.Locator(".portal-input").FocusAsync();

            // A window keeps its components, its state and its keyboard focus on screen 2.
            await page.EvaluateAsync("() => document.getElementById('move-to-screen-2').click()");
            await page.WaitForFunctionAsync(WindowOnScreen, 2);
            Assert.Equal(1, await page.Locator(".portal-probe").CountAsync());
            Assert.Equal(instance, await probe.GetAttributeAsync("data-instance"));
            Assert.Equal("2", await probe.Locator(".portal-count").TextContentAsync());
            Assert.Equal("kept", await probe.Locator(".portal-input").InputValueAsync());
            Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement?.classList.contains('portal-input') === true"));
            var screen2Box = (await page.Locator(".screen-portal-screen[data-screen='2']").BoundingBoxAsync())!;
            var windowBox = (await page.Locator("article.window-host").BoundingBoxAsync())!;
            Assert.InRange(windowBox.X, screen2Box.X, screen2Box.X + screen2Box.Width);

            // The renderer still updates it there, and its events still come back.
            await probe.Locator(".portal-increment").ClickAsync();
            Assert.Equal("3", await probe.Locator(".portal-count").TextContentAsync());

            await page.Locator("#move-to-screen-1").ClickAsync();
            await page.WaitForFunctionAsync(WindowOnScreen, 1);
            Assert.Equal(instance, await probe.GetAttributeAsync("data-instance"));
            Assert.Equal("3", await probe.Locator(".portal-count").TextContentAsync());

            // Screen 2's area goes away while the window is on it: the window comes back to screen 1.
            await page.Locator("#move-to-screen-2").ClickAsync();
            await page.WaitForFunctionAsync(WindowOnScreen, 2);
            await page.Locator("#toggle-screen-2").ClickAsync();
            await page.WaitForFunctionAsync(WindowOnScreen, 1);
            Assert.Equal(instance, await probe.GetAttributeAsync("data-instance"));
            Assert.Equal("3", await probe.Locator(".portal-count").TextContentAsync());

            // Screen 2 is shown again: the window, still on screen 2 for the runtime, goes back there.
            await page.Locator("#toggle-screen-2").ClickAsync();
            await page.WaitForFunctionAsync(WindowOnScreen, 2);
            Assert.Equal(instance, await probe.GetAttributeAsync("data-instance"));
            Assert.True(failures.Count == 0, $"Page failures ({failures.Count}):\n" + string.Join("\n", failures));
        }
        finally
        {
            StopProcess(server);
        }
    }
}
