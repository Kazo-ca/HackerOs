using System.Diagnostics;
using System.Globalization;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0116 as amended by ADR 0122: a window moved and resized with the pointer reopens with the
/// same size, at its cascade slot (the position is not remembered, as under GNOME), after being
/// closed, and again after the page is reloaded and the user signs back in, because the size lives
/// in <c>~/.local/state/kazoos/windows/&lt;appId&gt;.config</c> on the virtual disk.
/// </summary>
public sealed class WindowGeometryRestoreTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_resized_window_reopens_with_its_size_after_close_and_reload()
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
            output.WriteLine($"[test] opened at {opened}.");

            // Fractional coordinates followed by another drag protect the second-gesture regression.
            ILocator title = window.Locator("header.window-chrome h2");
            LocatorBoundingBoxResult titleBox = (await title.BoundingBoxAsync())!;
            await DragAsync(page, titleBox.X + 10, titleBox.Y + (titleBox.Height / 2), 120.5, 60.25, verifyReleaseFrames: true);
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-x", Format(opened.x + 120.5));
            titleBox = (await title.BoundingBoxAsync())!;
            await DragAsync(page, titleBox.X + 10, titleBox.Y + (titleBox.Height / 2), 10.5, 10.25, verifyReleaseFrames: true);
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-x", Format(opened.x + 131));

            LocatorBoundingBoxResult cornerBox = (await window.Locator(".resize-handle.resize-bottom-right").BoundingBoxAsync())!;
            await DragAsync(page, cornerBox.X + (cornerBox.Width / 2), cornerBox.Y + (cornerBox.Height / 2), 80, 40);
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-width", Format(opened.width + 80));
            (double x, double y, double width, double height) moved = await BoundsAsync(window);
            output.WriteLine($"[test] moved and resized to {moved}.");
            (double x, double y, double width, double height) expected = (opened.x, opened.y, moved.width, moved.height);

            await window.Locator("button.chrome-button-close").ClickAsync();
            await Assertions.Expect(window).ToHaveCountAsync(0, new() { Timeout = 15000 });

            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await Assertions.Expect(window).ToHaveCountAsync(1, new() { Timeout = 15000 });
            Assert.Equal(expected, await BoundsAsync(window));
            output.WriteLine("[test] the window reopened with its size, at its cascade slot.");

            // Give the grouped write time to land, then reload: the session ends, the file stays.
            await page.WaitForTimeoutAsync(1500);
            await page.ReloadAsync();
            ILocator passwordField = page.GetByLabel("Password", new() { Exact = true });
            await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
            await passwordField.FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await Assertions.Expect(window).ToHaveCountAsync(1, new() { Timeout = 15000 });
            Assert.Equal(expected, await BoundsAsync(window));
            output.WriteLine("[test] the window reopened with the same size after a reload.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private static async Task DragAsync(IPage page, double fromX, double fromY, double deltaX, double deltaY, bool verifyReleaseFrames = false)
    {
        await page.Mouse.MoveAsync((float)fromX, (float)fromY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)(fromX + deltaX), (float)(fromY + deltaY), new MouseMoveOptions { Steps = 8 });
        if (verifyReleaseFrames)
        {
            // Capture every browser frame across the real C# callback and OnAfterRender projection.
            await page.EvaluateAsync("""
                async () => {
                    await new Promise(requestAnimationFrame);
                    const host = document.querySelector('article.window-host');
                    const initial = host.getBoundingClientRect();
                    window.releaseFrames = { expected: [initial.x, initial.y], samples: [], running: true };
                    const sample = () => {
                        const rect = host.getBoundingClientRect();
                        releaseFrames.samples.push([rect.x, rect.y]);
                        if (releaseFrames.running) requestAnimationFrame(sample);
                    };
                    requestAnimationFrame(sample);
                }
                """);
        }
        await page.Mouse.UpAsync();
        if (verifyReleaseFrames)
        {
            await page.WaitForFunctionAsync("document.querySelector('article.window-host').style.getPropertyValue('--window-drag-translation') === ''");
            float[][] frames = await page.EvaluateAsync<float[][]>("""
                async () => {
                    await new Promise(requestAnimationFrame);
                    await new Promise(requestAnimationFrame);
                    releaseFrames.running = false;
                    return [releaseFrames.expected, ...releaseFrames.samples];
                }
                """);
            Assert.True(frames.Length > 1);
            Assert.All(frames, position => Assert.Equal(frames[0], position));
        }
    }

    private static async Task<(double X, double Y, double Width, double Height)> BoundsAsync(ILocator window) => (
        await ReadAsync(window, "data-window-x"),
        await ReadAsync(window, "data-window-y"),
        await ReadAsync(window, "data-window-width"),
        await ReadAsync(window, "data-window-height"));

    private static async Task<double> ReadAsync(ILocator window, string attribute) =>
        double.Parse((await window.GetAttributeAsync(attribute))!, CultureInfo.InvariantCulture);

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
