using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>Checks app-owned icons across desktop themes and the icon's context menu.</summary>
public sealed class WindowAppIconTests(ITestOutputHelper output)
{
    [Fact]
    public async Task App_icons_load_in_chrome_and_taskbar_and_only_right_click_opens_the_menu()
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
            ILocator window = page.Locator("article.window-host.is-focused");
            await Assertions.Expect(window).ToBeVisibleAsync();
            ILocator icon = window.Locator(".chrome-button-menu .window-icon");
            ILocator taskbarIcon = page.Locator(".taskbar-item.is-active .item-icon-image");
            const string editorIcon = "_content/HackerOs.Apps.TextEditor/icons/app.svg";
            await Assertions.Expect(icon).ToHaveAttributeAsync("src", editorIcon);
            await Assertions.Expect(taskbarIcon).ToHaveAttributeAsync("src", editorIcon);
            await WaitForImageAsync(icon);
            await WaitForImageAsync(taskbarIcon);

            foreach (string theme in new[] { "hackeros", "windows-98", "windows-xp", "windows-7", "windows-10", "ubuntu", "macos" })
            {
                await window.EvaluateAsync("(element, theme) => element.closest('[data-theme]').setAttribute('data-theme', theme)", theme);
                if (theme == "macos") await Assertions.Expect(icon).ToBeHiddenAsync();
                else await Assertions.Expect(icon).ToBeVisibleAsync();
                await Assertions.Expect(taskbarIcon).ToBeVisibleAsync();
                await Assertions.Expect(taskbarIcon).ToHaveAttributeAsync("src", editorIcon);
            }

            // The second app must use its own package icon rather than a shared generic glyph.
            await window.EvaluateAsync("element => element.closest('[data-theme]').setAttribute('data-theme', 'hackeros')");
            await E2ESupport.OpenAppAsync(page, "File Explorer", output);
            const string explorerIcon = "_content/HackerOs.Apps.FileExplorer/icons/app.svg";
            await Assertions.Expect(icon).ToHaveAttributeAsync("src", explorerIcon);
            await Assertions.Expect(taskbarIcon).ToHaveAttributeAsync("src", explorerIcon);
            await WaitForImageAsync(icon);
            await WaitForImageAsync(taskbarIcon);
            ILocator iconButton = window.Locator(".chrome-button-menu");
            ILocator menu = page.Locator("[data-context-menu='window-menu']");
            await iconButton.ClickAsync();
            await Assertions.Expect(menu).ToHaveCountAsync(0);
            await window.Locator("header h2").ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(menu).ToHaveCountAsync(0);
            await iconButton.ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(menu).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToHaveCountAsync(0);
            await page.Keyboard.PressAsync("Alt+F3");
            await Assertions.Expect(menu).ToBeVisibleAsync();
            Assert.Empty(errors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private static Task WaitForImageAsync(ILocator image) => image.EvaluateAsync("""
        async image => {
            await image.decode();
            if (!image.naturalWidth || !image.naturalHeight) throw new Error('App icon did not load');
        }
        """);
}
