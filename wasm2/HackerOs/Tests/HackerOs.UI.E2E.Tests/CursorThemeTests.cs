using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0205: a cursor theme chosen in Settings &gt; Appearance &gt; Cursors is drawn on the whole
/// surface at once (the document's arrow and every rule asking for <c>cursor: pointer</c>), and
/// choosing "System" gives the host's cursors back.
/// </summary>
public sealed class CursorThemeTests(ITestOutputHelper output)
{
    private const string DocumentCursor = "() => getComputedStyle(document.documentElement).cursor";

    private const string ButtonsWithNeonPointer =
        "() => [...document.querySelectorAll('button')].filter(b => getComputedStyle(b).cursor.includes('/cursors/hackeros-neon/pointer.png')).length";

    [Fact]
    public async Task Choosing_a_cursor_theme_draws_it_everywhere_and_system_restores_the_host_cursors()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);
        string screenshots = Path.Combine(solutionDirectory, "artifacts", "playwright", "cursors");
        Directory.CreateDirectory(screenshots);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1280, Height = 800 } });
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);
            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            Assert.Equal("auto", await page.EvaluateAsync<string>(DocumentCursor));

            await E2ESupport.OpenAppAsync(page, "Settings", output);
            ILocator cursors = page.GetByRole(AriaRole.Radiogroup, new() { Name = "Cursors" });
            await cursors.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

            await ChooseAsync(cursors, "HackerOS Neon");
            await page.WaitForFunctionAsync(
                "() => getComputedStyle(document.documentElement).cursor.includes('/cursors/hackeros-neon/default.png')",
                null, new PageWaitForFunctionOptions { Timeout = 15000 });
            Assert.EndsWith("2 1, default", await page.EvaluateAsync<string>(DocumentCursor), StringComparison.Ordinal);
            int themedButtons = await page.EvaluateAsync<int>(ButtonsWithNeonPointer);
            Assert.True(themedButtons > 0, "No button shows the theme's pointer.");
            await cursors.ScrollIntoViewIfNeededAsync();
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(screenshots, "settings-cursors-neon.png") });
            output.WriteLine($"[test] neon theme on the document and on {themedButtons} button(s).");

            await ChooseAsync(cursors, "System");
            await page.WaitForFunctionAsync(
                "() => getComputedStyle(document.documentElement).cursor === 'auto'",
                null, new PageWaitForFunctionOptions { Timeout = 15000 });
            Assert.Equal(0, await page.EvaluateAsync<int>(ButtonsWithNeonPointer));
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private static async Task ChooseAsync(ILocator group, string theme)
    {
        ILocator radio = group.GetByRole(AriaRole.Radio, new() { Name = theme });
        await radio.ClickAsync();
        await Assertions.Expect(radio).ToHaveAttributeAsync("aria-checked", "true", new() { Timeout = 15000 });
    }
}
