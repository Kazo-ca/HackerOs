using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

public sealed class MobileRecentAndOverlayBackTests(ITestOutputHelper output)
{
    /// <summary>
    /// End-to-end proof of the observable slice of <c>MOB-011</c> (plan §7.3/§7.5): the system
    /// Back triangle dismisses an open shell-owned overlay (notification shade, then Recent) ahead
    /// of anything else, Recent renders its empty state when nothing is open, and Back is a
    /// harmless no-op once nothing remains to dismiss. The multi-window Back-history/Recent-
    /// activation logic itself is proven at the unit level (<c>MobileNavigationHistoryTests</c>,
    /// <c>MobileNavigationCommandsAdapterTests</c>, <c>MobileRecentProjectionTests</c> against a
    /// real <see cref="Kazo.Os.Windowing.Core.WindowRuntime"/>). This scenario intentionally
    /// starts without apps; <see cref="MobileHomeScreenTests"/> covers actual mobile app launches.
    /// </summary>
    [Fact]
    public async Task Back_dismisses_shell_overlays_before_anything_else_and_recent_shows_its_empty_state()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using System.Diagnostics.Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

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

            await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
            ILocator desktopPanel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(desktopPanel).ToBeVisibleAsync();
            await desktopPanel.GetByRole(AriaRole.Radio, new() { Name = "Mobile" }).ClickAsync();

            ILocator backButton = page.GetByRole(AriaRole.Button, new() { Name = "Back" });
            ILocator recentButton = page.GetByRole(AriaRole.Button, new() { Name = "Recent" });
            await backButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            output.WriteLine("[test] Mobile shell active.");

            // Step 0: an open notification shade is dismissed by Back before anything else.
            ILocator shadeHandle = page.GetByRole(AriaRole.Button, new() { Name = "Notifications, calendar, and platform mode" });
            await shadeHandle.ClickAsync();
            ILocator mobilePanel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(mobilePanel).ToBeVisibleAsync();
            await backButton.ClickAsync();
            await Assertions.Expect(mobilePanel).ToHaveCountAsync(0);
            output.WriteLine("[test] Back dismissed the open notification shade.");

            // No app has been launched in this session, so Recent starts empty.
            await recentButton.ClickAsync();
            ILocator recentDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Recent" });
            await Assertions.Expect(recentDialog).ToBeVisibleAsync();
            await Assertions.Expect(recentDialog.GetByText("No recent apps")).ToBeVisibleAsync();
            output.WriteLine("[test] Recent surface opened, showing its empty state.");

            // Step 0 again: Back dismisses the open Recent surface itself before anything else.
            await backButton.ClickAsync();
            await Assertions.Expect(recentDialog).ToHaveCountAsync(0);
            output.WriteLine("[test] Back dismissed the open Recent surface.");

            // Step 5: at Home with nothing open and no overlay, Back is a harmless no-op.
            await backButton.ClickAsync();
            await Assertions.Expect(backButton).ToBeVisibleAsync();

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
