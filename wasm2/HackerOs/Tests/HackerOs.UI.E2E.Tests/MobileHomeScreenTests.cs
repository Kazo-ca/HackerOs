using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>Exercises the actual mobile Home, VFS persistence, touch/mouse gestures and shell switches.</summary>
public sealed class MobileHomeScreenTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "android")]
    [InlineData(true, "ios")]
    public async Task Home_drawer_pages_and_mouse_preferences_survive_reload(bool touch, string theme)
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
            if (theme == "ios")
            {
                await E2ESupport.OpenAppAsync(page, "Settings", output);
                await page.Locator("button.theme-option").Filter(new() { HasText = "iOS" }).ClickAsync();
                await Assertions.Expect(page.Locator(".settings-window-container")).ToHaveAttributeAsync("aria-busy", "false");
                await Assertions.Expect(page.Locator(".mobile-theme-options button.theme-option").Filter(new() { HasText = "iOS" })).ToHaveAttributeAsync("aria-pressed", "true");
                await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            }
            await SwitchToMobileAsync(page);
            output.WriteLine("[mobile] first mobile theme: " + await page.Locator(".theme-scope").GetAttributeAsync("data-theme"));
            ILocator shadeHandle = page.GetByRole(AriaRole.Button, new() { Name = "Notifications, calendar, and platform mode" });
            await Assertions.Expect(shadeHandle).ToHaveAttributeAsync("data-swipe-gesture-attached", "true");
            // A trusted click on the top handle must work, without a preceding swipe.
            await shadeHandle.ClickAsync();
            await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
                .GetByRole(AriaRole.Radio, new() { Name = "Desktop", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })).ToBeVisibleAsync();
            await SwitchToMobileAsync(page);
            await page.SetViewportSizeAsync(390, 844);
            ICDPSession? cdp = touch ? await page.Context.NewCDPSessionAsync(page) : null;
            if (cdp is not null) await cdp.SendAsync("Emulation.setTouchEmulationEnabled", new() { ["enabled"] = true, ["maxTouchPoints"] = 1 });

            ILocator home = page.Locator(".mobile-home-screen");
            await Assertions.Expect(home).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".theme-scope")).ToHaveAttributeAsync("data-theme", theme);
            ILocator systemHome = page.GetByRole(AriaRole.Button, new() { Name = "Home", Exact = true });
            await SwipeAsync(page, systemHome, 0, -150, cdp);
            ILocator drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "All applications", Exact = true });
            await Assertions.Expect(drawer).ToBeVisibleAsync();
            await Assertions.Expect(drawer.GetByRole(AriaRole.Searchbox)).ToBeFocusedAsync();
            await drawer.EvaluateAsync("async element => Promise.all(element.getAnimations().map(animation => animation.finished))");
            await page.ScreenshotAsync(new() { Path = Path.Combine(solution, "Tests", "TestResults", "mobile-home", $"{theme}-drawer.png") });
            await drawer.GetByRole(AriaRole.Searchbox, new() { Name = "Search applications" }).FillAsync("Calculator");
            output.WriteLine("[mobile] drawer opened; matching apps: " + await drawer.InnerTextAsync());
            ILocator addCalculator = drawer.GetByRole(AriaRole.Button, new() { Name = "Add Calculator to Home", Exact = true });
            await addCalculator.ClickAsync();
            await Assertions.Expect(addCalculator).ToBeDisabledAsync();
            await drawer.GetByRole(AriaRole.Button, new() { Name = "Pin Calculator to the dock", Exact = true }).ClickAsync();
            await Assertions.Expect(drawer.GetByRole(AriaRole.Button, new() { Name = "Unpin Calculator from the dock", Exact = true })).ToBeVisibleAsync();
            await drawer.GetByRole(AriaRole.Button, new() { Name = "Close application drawer" }).ClickAsync();
            ILocator calculator = home.Locator(".mobile-home-grid").GetByRole(AriaRole.Button, new() { Name = "Calculator", Exact = true });
            await Assertions.Expect(calculator).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(solution, "Tests", "TestResults", "mobile-home", $"{theme}-home.png") });
            if (cdp is not null)
            {
                LocatorBoundingBoxResult box = (await calculator.BoundingBoxAsync())!;
                await TouchAsync(cdp, "touchStart", box.X + box.Width / 2, box.Y + 15);
                await page.WaitForTimeoutAsync(650);
                await cdp.SendAsync("Input.dispatchTouchEvent", new() { ["type"] = "touchEnd", ["touchPoints"] = Array.Empty<object>() });
            }
            else await home.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
            await Assertions.Expect(home.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true })).ToBeVisibleAsync();
            await home.GetByRole(AriaRole.Button, new() { Name = "New page", Exact = true }).ClickAsync();
            await Assertions.Expect(home.Locator(".mobile-page-dot.is-current")).ToHaveAttributeAsync("aria-label", "Page 2");
            await home.GetByRole(AriaRole.Button, new() { Name = "All applications", Exact = true }).ClickAsync();
            await drawer.GetByRole(AriaRole.Searchbox, new() { Name = "Search applications" }).FillAsync("Browser");
            await drawer.GetByRole(AriaRole.Button, new() { Name = "Add Browser to Home", Exact = true }).ClickAsync();
            await Assertions.Expect(drawer.GetByRole(AriaRole.Button, new() { Name = "Add Browser to Home", Exact = true })).ToBeDisabledAsync();
            await drawer.GetByRole(AriaRole.Button, new() { Name = "Close application drawer" }).ClickAsync();
            await home.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
            await SwipeAsync(page, home.Locator(".mobile-home-pages"), 170, 0, cdp);
            await Assertions.Expect(calculator).ToBeVisibleAsync();
            await SwipeAsync(page, home.Locator(".mobile-home-pages"), -170, 0, cdp);
            await Assertions.Expect(home.Locator(".mobile-home-grid").GetByRole(AriaRole.Button, new() { Name = "Browser", Exact = true })).ToBeVisibleAsync();
            await home.GetByRole(AriaRole.Button, new() { Name = "Previous Home page" }).ClickAsync();
            await Assertions.Expect(calculator).ToBeVisibleAsync();
            await home.GetByRole(AriaRole.Button, new() { Name = "Next Home page" }).ClickAsync();

            await home.Locator(".mobile-app-dock").GetByRole(AriaRole.Button, new() { Name = "Settings", Exact = true }).ClickAsync();
            ILocator mouseHelpers = page.GetByLabel("Show mobile mouse helpers", new() { Exact = true });
            await mouseHelpers.UncheckAsync();
            await Assertions.Expect(page.Locator(".mobile-home-screen.has-mouse-helpers")).ToHaveCountAsync(0);
            await systemHome.ClickAsync();
            await Assertions.Expect(home).ToBeVisibleAsync();
            await Assertions.Expect(home.Locator(".mobile-page-arrow")).ToHaveCountAsync(0);
            await SwipeAsync(page, home.Locator(".mobile-home-pages"), 170, 0, cdp);
            await Assertions.Expect(calculator).ToBeVisibleAsync();

            await page.ReloadAsync();
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
            await Assertions.Expect(home).ToBeVisibleAsync(new() { Timeout = 20000 });
            await Assertions.Expect(calculator).ToBeVisibleAsync();
            await Assertions.Expect(home.Locator(".mobile-app-dock").GetByRole(AriaRole.Button, new() { Name = "Calculator", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(home.Locator(".mobile-page-arrow")).ToHaveCountAsync(0);
            await SwipeAsync(page, home.Locator(".mobile-home-pages"), -170, 0, cdp);
            await Assertions.Expect(home.Locator(".mobile-home-grid").GetByRole(AriaRole.Button, new() { Name = "Browser", Exact = true })).ToBeVisibleAsync();
            // The mobile layout is an ordinary hidden VFS folder, accessible with an explicit opt-in.
            await page.GetByRole(AriaRole.Button, new() { Name = "Notifications, calendar, and platform mode" }).ClickAsync();
            await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
                .GetByRole(AriaRole.Radio, new() { Name = "Desktop", Exact = true }).ClickAsync();
            await page.SetViewportSizeAsync(1400, 900);
            await E2ESupport.OpenAppAsync(page, "File Explorer", output);
            ILocator addressInput = page.Locator(".address-input");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath);
            await Assertions.Expect(page.Locator("tr.entry-row").Filter(new() { HasText = "Documents" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".desktop-surface .file-view-desktop")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".desktop-surface").GetByRole(AriaRole.Option, new() { Name = ".mobile", Exact = true })).ToHaveCountAsync(0);
            await addressInput.FillAsync(E2ESupport.HomePath + "/Desktop");
            await addressInput.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".file-view-breadcrumb")).ToContainTextAsync("Desktop");
            ILocator mobileFolder = page.Locator(".file-explorer-container [data-item-path$='/.mobile']");
            await Assertions.Expect(mobileFolder).ToHaveCountAsync(0);
            ILocator hiddenToggle = page.GetByRole(AriaRole.Button, new() { Name = "Show hidden files and folders", Exact = true });
            await hiddenToggle.ClickAsync();
            await Assertions.Expect(mobileFolder).ToBeVisibleAsync();
            await mobileFolder.DblClickAsync();
            await Assertions.Expect(page.Locator("[data-item-path$='/.mobile/layout.conf']")).ToBeVisibleAsync();
            await addressInput.FillAsync(E2ESupport.HomePath + "/Desktop");
            await addressInput.PressAsync("Enter");
            await Assertions.Expect(mobileFolder).ToBeVisibleAsync();
            await hiddenToggle.ClickAsync();
            await Assertions.Expect(mobileFolder).ToHaveCountAsync(0);
            Assert.Empty(errors);
        }
        finally { E2ESupport.StopProcess(server); }
    }

    /// <summary>ADR 0217: the mobile home screen shows its own wallpaper, the desktop keeps the theme's background.</summary>
    [Fact]
    public async Task The_mobile_wallpaper_is_drawn_behind_the_home_screen_only()
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

            await E2ESupport.OpenAppAsync(page, "Settings", output);
            ILocator panel = page.Locator(".wallpaper-panel");
            await panel.GetByRole(AriaRole.Radio, new() { Name = "Mobile", Exact = true }).ClickAsync();
            await Assertions.Expect(panel.GetByRole(AriaRole.Radio, new() { Name = "Mobile theme background" })).ToBeVisibleAsync();
            await panel.GetByRole(AriaRole.Radio, new() { Name = "Aurora", Exact = true }).ClickAsync();
            await Assertions.Expect(panel.GetByRole(AriaRole.Radio, new() { Name = "Aurora", Exact = true })).ToHaveAttributeAsync("aria-checked", "true");
            await page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            // The desktop's own choice stays the theme's background.
            Assert.Equal(0, await page.Locator(".desktop-wallpaper").CountAsync());

            await SwitchToMobileAsync(page);
            await page.SetViewportSizeAsync(390, 844);
            ILocator wallpaper = page.Locator(".mobile-shell-workspace > .desktop-wallpaper");
            await Assertions.Expect(wallpaper).ToHaveAttributeAsync("data-wallpaper", "builtin:aurora");
            await Assertions.Expect(wallpaper).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("fit-fill"));
            await Assertions.Expect(wallpaper).ToHaveCSSAsync("background-image", new System.Text.RegularExpressions.Regex("aurora\\.webp"));
            await Assertions.Expect(page.Locator(".mobile-home-screen")).ToHaveCSSAsync("background-image", "none");
            Directory.CreateDirectory(Path.Combine(solution, "Tests", "TestResults", "mobile-home"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(solution, "Tests", "TestResults", "mobile-home", "mobile-wallpaper.png") });
            Assert.Empty(errors);
        }
        finally { E2ESupport.StopProcess(server); }
    }

    private static async Task SwitchToMobileAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
            .GetByRole(AriaRole.Radio, new() { Name = "Mobile", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".mobile-home-screen")).ToBeVisibleAsync();
    }

    private static async Task SwipeAsync(IPage page, ILocator target, double dx, double dy, ICDPSession? touch)
    {
        LocatorBoundingBoxResult box = (await target.BoundingBoxAsync())!;
        double x = box.X + box.Width / 2;
        double y = box.Y + box.Height * .7;
        if (touch is null)
        {
            await page.Mouse.MoveAsync((float)x, (float)y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync((float)(x + dx), (float)(y + dy), new() { Steps = 8 });
            await page.Mouse.UpAsync();
        }
        else
        {
            await TouchAsync(touch, "touchStart", x, y);
            for (int step = 1; step <= 8; step++) await TouchAsync(touch, "touchMove", x + dx * step / 8, y + dy * step / 8);
            await touch.SendAsync("Input.dispatchTouchEvent", new() { ["type"] = "touchEnd", ["touchPoints"] = Array.Empty<object>() });
        }
    }
    private static Task TouchAsync(ICDPSession session, string type, double x, double y) => session.SendAsync("Input.dispatchTouchEvent", new()
    {
        ["type"] = type,
        ["touchPoints"] = new[] { new { x, y, id = 1 } }
    });
}
