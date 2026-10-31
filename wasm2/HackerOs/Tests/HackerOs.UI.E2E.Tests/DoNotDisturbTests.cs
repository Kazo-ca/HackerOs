using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

public sealed class DoNotDisturbTests(ITestOutputHelper output)
{
    // The whole suite runs one browser harness after another: a step can take long under that load.
    private const int StepTimeoutMs = 45000;

    /// <summary>
    /// ADR 0216: the clock panel's "Do not disturb" switch shows a moon in the taskbar's
    /// notification area, the choice survives a reload (it is a file in ~/.config/hackeros), the
    /// moon opens the clock panel, and turning the switch off removes the moon.
    /// </summary>
    [Fact]
    public async Task The_clock_panel_switch_shows_a_moon_that_survives_a_reload()
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

            ILocator moon = page.GetByRole(AriaRole.Button, new() { Name = "Do not disturb is on" });
            await Assertions.Expect(moon).ToHaveCountAsync(0);

            ILocator panel = await OpenClockPanelAsync(page);
            ILocator dndSwitch = panel.GetByRole(AriaRole.Switch, new() { Name = "Do not disturb" });
            await Assertions.Expect(dndSwitch).Not.ToBeCheckedAsync(new() { Timeout = StepTimeoutMs });
            await dndSwitch.CheckAsync(new LocatorCheckOptions { Force = true });
            await Assertions.Expect(dndSwitch).ToBeCheckedAsync(new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(moon).ToBeVisibleAsync(new() { Timeout = StepTimeoutMs });
            output.WriteLine("[test] Do not disturb on: the moon is in the notification area.");

            await page.ReloadAsync();
            ILocator passwordField = page.GetByLabel("Password", new() { Exact = true });
            await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs });
            await passwordField.FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
            await Assertions.Expect(moon).ToBeVisibleAsync(new() { Timeout = StepTimeoutMs });
            output.WriteLine("[test] the moon is back after a reload.");

            // The moon opens the clock panel, which holds the switch.
            await moon.ClickAsync();
            panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = StepTimeoutMs });
            dndSwitch = panel.GetByRole(AriaRole.Switch, new() { Name = "Do not disturb" });
            await Assertions.Expect(dndSwitch).ToBeCheckedAsync(new() { Timeout = StepTimeoutMs });
            await dndSwitch.UncheckAsync(new LocatorUncheckOptions { Force = true });
            await Assertions.Expect(moon).ToHaveCountAsync(0, new() { Timeout = StepTimeoutMs });
            output.WriteLine("[test] Do not disturb off: the moon left.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private static async Task<ILocator> OpenClockPanelAsync(IPage page)
    {
        await page.Locator(".taskbar-clock").ClickAsync();
        ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
        await Assertions.Expect(panel).ToBeVisibleAsync(new() { Timeout = StepTimeoutMs });
        return panel;
    }
}
