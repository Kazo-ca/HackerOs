using Microsoft.Playwright;

namespace HackerOs.E2E.Tests;

/// <summary>Dragging a window from one screen to another (ADR 0183, doc/en/wasm/dev/drag-between-screens.md).</summary>
public sealed partial class IndexedDbBrowserContractTests
{
    /// <summary>
    /// Verifies that a window whose title bar is released over screen 2 changes screen without being
    /// recreated and lands where it was dropped, that it comes back the same way, and that a window
    /// released between the screens stays on its own screen.
    /// </summary>
    [Fact]
    public async Task Window_dragged_by_its_title_bar_changes_screen_where_it_is_dropped()
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

            ILocator window = page.Locator("article.window-host");
            ILocator titleBar = window.Locator(".window-chrome h2");
            var screen1 = (await page.Locator(".screen-portal-screen[data-screen='1'] .window-layer").BoundingBoxAsync())!;
            var screen2 = (await page.Locator(".screen-portal-screen[data-screen='2'] .window-layer").BoundingBoxAsync())!;

            // Grab the title bar and drop it 150,80 into screen 2: the window keeps the grab offset.
            var before = (await window.BoundingBoxAsync())!;
            var title = (await titleBar.BoundingBoxAsync())!;
            float grabX = title.X + 20;
            float grabY = title.Y + title.Height / 2;
            await DragAsync(page, grabX, grabY, screen2.X + 150, screen2.Y + 80);
            await page.WaitForFunctionAsync(WindowOnScreen, 2);
            Assert.Equal(instance, await probe.GetAttributeAsync("data-instance"));
            Assert.Equal("1", await probe.Locator(".portal-count").TextContentAsync());
            var dropped = (await window.BoundingBoxAsync())!;
            Assert.InRange(dropped.X, screen2.X + 150 - (grabX - before.X) - 2, screen2.X + 150 - (grabX - before.X) + 2);
            Assert.InRange(dropped.Y, screen2.Y + 80 - (grabY - before.Y) - 2, screen2.Y + 80 - (grabY - before.Y) + 2);

            // Still a live window on screen 2.
            await probe.Locator(".portal-increment").ClickAsync();
            Assert.Equal("2", await probe.Locator(".portal-count").TextContentAsync());

            // Released in the gap between the screens: no screen under the pointer, it stays on screen 2.
            title = (await titleBar.BoundingBoxAsync())!;
            await DragAsync(page, title.X + 20, title.Y + title.Height / 2, screen1.X + screen1.Width + 10, title.Y + title.Height / 2);
            await page.WaitForTimeoutAsync(300);
            Assert.True(await page.EvaluateAsync<bool>(WindowOnScreen, 2));

            // And back to screen 1, the same way.
            title = (await titleBar.BoundingBoxAsync())!;
            await DragAsync(page, title.X + 20, title.Y + title.Height / 2, screen1.X + 100, screen1.Y + 100);
            await page.WaitForFunctionAsync(WindowOnScreen, 1);
            Assert.Equal(instance, await probe.GetAttributeAsync("data-instance"));
            Assert.Equal("2", await probe.Locator(".portal-count").TextContentAsync());
            Assert.Equal(0, await page.Locator(".window-drop-preview").CountAsync());
            Assert.True(failures.Count == 0, $"Page failures ({failures.Count}):\n" + string.Join("\n", failures));
        }
        finally
        {
            StopProcess(server);
        }
    }

    private static async Task DragAsync(IPage page, float fromX, float fromY, float toX, float toY)
    {
        await page.Mouse.MoveAsync(fromX, fromY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(toX, toY, new MouseMoveOptions { Steps = 12 });
        await page.Mouse.UpAsync();
    }
}
