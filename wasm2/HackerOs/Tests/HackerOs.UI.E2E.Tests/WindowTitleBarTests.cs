using System.Diagnostics;
using System.Globalization;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0177: the title bar buttons of a maximized or snapped window answer clicks over their whole
/// surface (no resize handle sits over them), a double click on the title bar toggles maximize, and
/// dragging the title bar of a maximized or snapped window gives it back its earlier size.
/// </summary>
public sealed class WindowTitleBarTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Maximized_and_snapped_windows_answer_their_title_bar()
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

            await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1400, Height = 900 },
            });
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            ILocator window = page.Locator("article.window-host");
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await Assertions.Expect(window).ToHaveCountAsync(1, new() { Timeout = 15000 });
            (double x, double y, double width, double height) opened = await BoundsAsync(window);
            ILocator title = window.Locator("header.window-chrome h2");

            // A double click on the title bar maximizes; nothing is left over the buttons.
            await title.DblClickAsync();
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-state", "Maximized");
            await Assertions.Expect(window.Locator(".resize-handle")).ToHaveCountAsync(0);
            foreach (string button in new[] { "minimize", "maximize", "close" })
            {
                Assert.True(await CoversWholeSurfaceAsync(page, window.Locator($"button.chrome-button-{button}")),
                    $"Something sits over the {button} button of a maximized window.");
            }

            // The restore button answers a click at its very top edge, where the resize handles were.
            await ClickTopEdgeAsync(page, window.Locator("button.chrome-button-maximize"));
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-state", "Normal");
            Assert.Equal(opened, await BoundsAsync(window));

            // Double click again: maximized; a drag of the title bar restores the size under the pointer.
            await title.DblClickAsync();
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-state", "Maximized");
            LocatorBoundingBoxResult titleBox = (await title.BoundingBoxAsync())!;
            await DragAsync(page, titleBox.X + 40, titleBox.Y + (titleBox.Height / 2), 250, 200);
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-state", "Normal");
            (double x, double y, double width, double height) dragged = await BoundsAsync(window);
            Assert.Equal((opened.width, opened.height), (dragged.width, dragged.height));
            Assert.True(dragged.y > 100, $"The restored window should follow the pointer, got {dragged}.");
            output.WriteLine($"[test] dragged out of maximize to {dragged}.");

            // Snap left: the window remembers its size; its close-side buttons answer at the top edge.
            await title.ClickAsync();
            await page.Keyboard.PressAsync("Alt+Shift+ArrowLeft");
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-snapped", "true");
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-x", "0");
            Assert.True(await CoversWholeSurfaceAsync(page, window.Locator("button.chrome-button-maximize")),
                "Something sits over the maximize button of a snapped window.");

            // Maximize then restore: the snap is undone.
            await ClickTopEdgeAsync(page, window.Locator("button.chrome-button-maximize"));
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-state", "Maximized");
            await window.Locator("button.chrome-button-maximize").ClickAsync();
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-state", "Normal");
            await Assertions.Expect(window).Not.ToHaveAttributeAsync("data-window-snapped", "true");
            Assert.Equal(dragged, await BoundsAsync(window));

            // Snap right, then drag the title bar: back to the earlier size.
            await title.ClickAsync();
            await page.Keyboard.PressAsync("Alt+Shift+ArrowRight");
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-snapped", "true");
            titleBox = (await title.BoundingBoxAsync())!;
            await DragAsync(page, titleBox.X + 40, titleBox.Y + (titleBox.Height / 2), -200, 150);
            await Assertions.Expect(window).Not.ToHaveAttributeAsync("data-window-snapped", "true");
            (double x, double y, double width, double height) unsnapped = await BoundsAsync(window);
            Assert.Equal((opened.width, opened.height), (unsnapped.width, unsnapped.height));

            // Snapped again: the close button closes it.
            await title.ClickAsync();
            await page.Keyboard.PressAsync("Alt+Shift+ArrowLeft");
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-snapped", "true");
            await ClickTopEdgeAsync(page, window.Locator("button.chrome-button-close"));
            await Assertions.Expect(window).ToHaveCountAsync(0, new() { Timeout = 15000 });

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Whether every sampled point of <paramref name="button"/> hits the button itself.</summary>
    private static async Task<bool> CoversWholeSurfaceAsync(IPage page, ILocator button)
    {
        LocatorBoundingBoxResult box = (await button.BoundingBoxAsync())!;
        string className = (await button.GetAttributeAsync("class"))!.Split(' ').Last();
        for (double fx = 0.05; fx < 1; fx += 0.15)
        {
            for (double fy = 0.05; fy < 1; fy += 0.15)
            {
                string x = (box.X + (box.Width * fx)).ToString(CultureInfo.InvariantCulture);
                string y = (box.Y + (box.Height * fy)).ToString(CultureInfo.InvariantCulture);
                bool hit = await page.EvaluateAsync<bool>(
                    $"() => !!document.elementFromPoint({x}, {y})?.closest('button.{className}')");
                if (!hit)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Clicks one pixel under the top edge of <paramref name="button"/>.</summary>
    private static async Task ClickTopEdgeAsync(IPage page, ILocator button)
    {
        LocatorBoundingBoxResult box = (await button.BoundingBoxAsync())!;
        await page.Mouse.ClickAsync((float)(box.X + (box.Width / 2)), (float)(box.Y + 1));
    }

    private static async Task DragAsync(IPage page, double fromX, double fromY, double deltaX, double deltaY)
    {
        await page.Mouse.MoveAsync((float)fromX, (float)fromY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(fromX + deltaX), (float)(fromY + deltaY), new MouseMoveOptions { Steps = 12 });
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("document.querySelector('article.window-host').style.getPropertyValue('--window-drag-translation') === ''");
    }

    private static async Task<(double X, double Y, double Width, double Height)> BoundsAsync(ILocator window) => (
        await ReadAsync(window, "data-window-x"),
        await ReadAsync(window, "data-window-y"),
        await ReadAsync(window, "data-window-width"),
        await ReadAsync(window, "data-window-height"));

    private static async Task<double> ReadAsync(ILocator window, string attribute) =>
        double.Parse((await window.GetAttributeAsync(attribute))!, CultureInfo.InvariantCulture);
}
