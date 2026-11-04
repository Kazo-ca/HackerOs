using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0117 §4: Hack Paint writes its whole window title through the SDK
/// (<c>WindowAppBase.SetWindowTitleAsync</c>), and the window manager shows it in the title bar
/// and the taskbar, with <c>*</c> while the image has unsaved changes.
/// </summary>
public sealed class HackPaintWindowTitleTests(ITestOutputHelper output)
{
    private const string ImageFileName = "e2e-title.png";

    [Fact]
    public async Task Hack_paint_shows_its_document_and_unsaved_state_in_the_window_title_and_taskbar()
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
            await E2ESupport.OpenAppAsync(page, "Hack Paint", output);

            ILocator windowTitle = page.Locator("header.window-chrome h2", new() { HasText = "Hack Paint" });
            ILocator taskbarTitle = page.Locator(".taskbar-item .item-title", new() { HasText = "Hack Paint" });

            // A new image: named by the app before anything is drawn.
            await Assertions.Expect(windowTitle).ToHaveTextAsync("Untitled — Hack Paint", new() { Timeout = 15000 });
            await Assertions.Expect(taskbarTitle).ToHaveTextAsync("Untitled — Hack Paint");
            output.WriteLine("[test] new image titled 'Untitled — Hack Paint'.");

            // A stroke marks the image as modified.
            await DrawStrokeAsync(page);
            await Assertions.Expect(windowTitle).ToHaveTextAsync("*Untitled — Hack Paint", new() { Timeout = 15000 });
            await Assertions.Expect(taskbarTitle).ToHaveTextAsync("*Untitled — Hack Paint");
            output.WriteLine("[test] modified image titled '*Untitled — Hack Paint'.");

            // Saving gives the window the file's name and clears the marker.
            await page.Locator("#paint-btn-save-as").ClickAsync();
            ILocator saveDialog = page.Locator("section.file-dialog", new() { HasText = "Save Image" });
            await saveDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await saveDialog.GetByLabel("File name").FillAsync(ImageFileName);
            await saveDialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await saveDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15000 });

            await Assertions.Expect(windowTitle).ToHaveTextAsync($"{ImageFileName} — Hack Paint", new() { Timeout = 15000 });
            await Assertions.Expect(taskbarTitle).ToHaveTextAsync($"{ImageFileName} — Hack Paint");
            output.WriteLine($"[test] saved image titled '{ImageFileName} — Hack Paint'.");

            // Another stroke: the saved file is modified again.
            await DrawStrokeAsync(page);
            await Assertions.Expect(windowTitle).ToHaveTextAsync($"*{ImageFileName} — Hack Paint", new() { Timeout = 15000 });

            // The window's accessible name follows the title (ADR 0117 §3).
            ILocator window = page.Locator("article[role=dialog]", new() { Has = windowTitle });
            string? labelledBy = await window.GetAttributeAsync("aria-labelledby");
            Assert.False(string.IsNullOrEmpty(labelledBy));
            Assert.Equal($"*{ImageFileName} — Hack Paint", await page.Locator($"[id='{labelledBy}']").InnerTextAsync());

            // The file name left the app's own toolbar: only the window manager shows it.
            Assert.Equal(0, await page.Locator("#paint-title").CountAsync());
            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private static async Task DrawStrokeAsync(IPage page)
    {
        ILocator canvas = page.Locator("canvas.paint-canvas");
        await canvas.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        LocatorBoundingBoxResult box = await canvas.BoundingBoxAsync() ?? throw new InvalidOperationException("Canvas has no box.");
        float startX = box.X + 40;
        float startY = box.Y + 40;
        await page.Mouse.MoveAsync(startX, startY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(startX + 60, startY + 30, new MouseMoveOptions { Steps = 6 });
        await page.Mouse.UpAsync();
    }
}
