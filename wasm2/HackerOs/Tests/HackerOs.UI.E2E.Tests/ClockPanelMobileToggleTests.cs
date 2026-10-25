using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

public sealed class ClockPanelMobileToggleTests(ITestOutputHelper output)
{
    /// <summary>
    /// End-to-end proof of the mobile-platform toggle location
    /// (doc/wasm/dev/old/mobile-interface-platform-plan.md §16.1) and, since <c>MOB-008</c>, that the choice
    /// actually drives which shell renders: clicking the taskbar clock opens a panel whose
    /// Auto/Desktop/Mobile choice persists across a reload, and after reload the Mobile shell (its
    /// Back/Home/Recent system navigation bar, not the Desktop taskbar/App launcher) is what's
    /// reached.
    /// </summary>
    [Fact]
    public async Task Clock_panel_opens_and_the_mobile_choice_persists_across_reload_into_the_mobile_shell()
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

            ILocator clockButton = page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" });
            await clockButton.ClickAsync();

            ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(panel).ToBeVisibleAsync();
            output.WriteLine("[test] clock panel opened.");

            ILocator mobileOption = panel.GetByRole(AriaRole.Radio, new() { Name = "Mobile" });
            await mobileOption.ClickAsync();
            output.WriteLine("[test] selected explicit Mobile platform preference.");

            // Selecting Mobile now drives a live shell swap (MOB-008) — the panel and the whole
            // Desktop shell it belongs to are torn down as part of that swap, so nothing further is
            // asserted about the panel itself here; PlatformShellSwitchTests covers the live
            // (no-reload) swap directly. This test's own focus is that the choice survives a reload
            // into the same Mobile shell. The swap happens once the choice is written to
            // ~/.config/hackeros (a settings file on the virtual disk, ADR 0044), which takes a
            // few hundred milliseconds in the browser (ADR 0090); reloading before that would test a write that
            // never finished, as pulling the plug mid-save would on a real machine. So the
            // reload waits for the Mobile shell to appear first.
            await page.GetByRole(AriaRole.Button, new() { Name = "Back" })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            output.WriteLine("[test] live swap to the Mobile shell done; reloading.");
            await page.ReloadAsync();

            // The local profile persists across reload, but the session does not — reload lands
            // on the sign-in view for the existing account, not straight back on the desktop.
            ILocator passwordField = page.GetByLabel("Password", new() { Exact = true });
            await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
            await passwordField.FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();

            // The persisted Mobile preference now drives the actual shell (MOB-008): the Desktop
            // taskbar/App launcher is gone, replaced by the Mobile system navigation bar.
            ILocator backButton = page.GetByRole(AriaRole.Button, new() { Name = "Back" });
            await backButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Home", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Recent" })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })).ToHaveCountAsync(0);
            output.WriteLine("[test] Mobile shell reached after reload — system navigation bar visible, Desktop taskbar gone.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
