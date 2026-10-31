using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0199: hovering a taskbar button shows a reduced live copy of its window, resting on the
/// preview peeks at the window, and the preview's × closes it.
/// </summary>
public sealed class TaskbarWindowPreviewTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Hovering_a_button_previews_its_window_peeks_at_it_and_closes_it()
    {
        string solution = E2ESupport.FindSolutionDirectory();
        string address = $"http://127.0.0.1:{E2ESupport.ReservePort()}";
        using Process server = E2ESupport.StartHarness(solution, address, testDemo: false, output);
        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
            IPage page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1400, Height = 900 } });
            List<string> errors = E2ESupport.AttachDiagnostics(page, output);
            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await Assertions.Expect(page.Locator(".taskbar-item")).ToHaveCountAsync(1);
            await Assertions.Expect(page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" })).ToHaveCountAsync(0);
            await E2ESupport.OpenAppAsync(page, "File Explorer", output);
            await Assertions.Expect(page.Locator(".taskbar-item")).ToHaveCountAsync(2);

            ILocator editorButton = page.Locator(".taskbar-item", new() { HasText = "Text Editor" });
            ILocator preview = page.Locator(".taskbar-preview");
            await editorButton.HoverAsync();
            await Assertions.Expect(preview).ToBeVisibleAsync();
            await Assertions.Expect(preview.Locator(".taskbar-preview-title")).ToContainTextAsync("Text Editor");

            // The live copy: a reduced clone of the window, with no id and nothing focusable.
            ILocator copy = preview.Locator(".taskbar-preview-live > article");
            await Assertions.Expect(copy).ToHaveCountAsync(1);
            await Assertions.Expect(preview).ToHaveAttributeAsync("data-live-copy", "");
            Assert.Equal(0, await preview.Locator(".taskbar-preview-live [id]").CountAsync());
            Assert.Equal(0, await preview.Locator(".taskbar-preview-live [data-window-id]").CountAsync());
            Assert.Equal(2, await page.Locator("article.window-host[data-window-id]").CountAsync());

            // Resting on the preview peeks at the editor: the explorer keeps only its outline.
            await preview.Locator(".taskbar-preview-thumbnail").HoverAsync();
            await Assertions.Expect(page.Locator("article.window-host[data-window-id].is-peek-shown")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("article.window-host[data-window-id].is-peek-see-through")).ToHaveCountAsync(1);

            // Leaving it ends the peek and closes the preview.
            await page.Mouse.MoveAsync(700, 300);
            await Assertions.Expect(page.Locator("article.window-host[class*='is-peek-']")).ToHaveCountAsync(0);
            await Assertions.Expect(preview).ToHaveCountAsync(0);

            // A minimized window shows in place while the user peeks at it.
            ILocator explorerButton = page.Locator(".taskbar-item", new() { HasText = "File Explorer" });
            await explorerButton.ClickAsync();
            await Assertions.Expect(explorerButton).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("is-minimized"));
            await explorerButton.HoverAsync();
            await Assertions.Expect(preview).ToBeVisibleAsync();
            await preview.Locator(".taskbar-preview-thumbnail").HoverAsync();
            await Assertions.Expect(page.Locator("article.window-host[data-window-id].is-peek-shown")).ToBeVisibleAsync();
            await page.Mouse.MoveAsync(700, 300);
            await Assertions.Expect(page.Locator("article.window-host[hidden]")).ToBeHiddenAsync();

            // The preview's × closes the window.
            await editorButton.HoverAsync();
            await Assertions.Expect(preview).ToBeVisibleAsync();
            await Assertions.Expect(preview.Locator(".taskbar-preview-close")).ToHaveAttributeAsync("aria-label", "Close Untitled — Text Editor");
            await preview.Locator(".taskbar-preview-close").ClickAsync();
            await Assertions.Expect(editorButton).ToHaveCountAsync(0);
            await Assertions.Expect(preview).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator("article.window-host[data-window-id]")).ToHaveCountAsync(1);
            Assert.Empty(errors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
