using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

public sealed class PlatformShellSwitchTests(ITestOutputHelper output)
{
    /// <summary>
    /// End-to-end proof of the controlled platform switch (doc/en/wasm/dev/old/mobile-interface-platform-plan.md
    /// §6.3, <c>MOB-008</c>): selecting Mobile in the clock panel swaps the rendered shell live
    /// (no reload) from the Desktop taskbar to the Mobile system navigation bar, and Mobile's
    /// swipe-down notification shade (reusing the same clock panel) swaps it back.
    /// </summary>
    [Fact]
    public async Task Selecting_mobile_swaps_the_live_shell_and_switching_back_restores_desktop()
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

            ILocator appLauncher = page.GetByRole(AriaRole.Button, new() { Name = "App launcher" });
            await Assertions.Expect(appLauncher).ToBeVisibleAsync();
            output.WriteLine("[test] Desktop shell reached — App launcher visible.");

            await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
            ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(panel).ToBeVisibleAsync();
            await panel.GetByRole(AriaRole.Radio, new() { Name = "Mobile" }).ClickAsync();

            // No reload: the shell swap is live, driven by UiPlatformPreferenceService.Changed.
            ILocator backButton = page.GetByRole(AriaRole.Button, new() { Name = "Back" });
            await backButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Home", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Recent" })).ToBeVisibleAsync();
            await Assertions.Expect(appLauncher).ToHaveCountAsync(0);
            output.WriteLine("[test] Mobile shell swapped in live — system navigation bar visible, Desktop taskbar gone.");

            await page.GetByRole(AriaRole.Button, new() { Name = "Notifications, calendar, and platform mode" }).ClickAsync();
            ILocator mobileShadePanel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(mobileShadePanel).ToBeVisibleAsync();
            output.WriteLine("[test] Mobile notification shade opened — reuses the same clock panel.");

            await mobileShadePanel.GetByRole(AriaRole.Radio, new() { Name = "Desktop" }).ClickAsync();

            await Assertions.Expect(appLauncher).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(backButton).ToHaveCountAsync(0);
            output.WriteLine("[test] Desktop shell restored — App launcher back, Mobile nav bar gone.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
