using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0195: the session's Power button left the taskbar for the Start menu. Its menu offers
/// « Sign out », « Switch user » (ADR 0201, see <see cref="StartMenuSwitchUserTests"/>) and
/// « Restart », which signs out like « Sign out » and then reloads the page.
/// </summary>
public sealed class StartMenuPowerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Power_menu_of_the_start_menu_signs_out_and_restarts()
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

            // The taskbar no longer has its own session button.
            await Assertions.Expect(page.Locator("footer.taskbar-container .logout-trigger")).ToHaveCountAsync(0);

            ILocator menu = await OpenPowerMenuAsync(page);
            await Assertions.Expect(menu.GetByRole(AriaRole.Menuitem, new() { Name = "Switch user" })).ToBeEnabledAsync();
            await Assertions.Expect(menu.GetByRole(AriaRole.Menuitem, new() { Name = "Restart" })).ToBeEnabledAsync();

            // Escape closes the Power menu first, the Start menu stays open.
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(menu).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Power" })).ToBeVisibleAsync();
            await page.Keyboard.PressAsync("Escape");

            menu = await OpenPowerMenuAsync(page);
            await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).ClickAsync();
            ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Sign out of HackerOS" });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
            await SignInAgainAsync(page);
            output.WriteLine("[test] sign-out from the Power menu reached the sign-in screen.");

            // « Restart » signs out the same way, then reloads the page: the marker set on this
            // page is gone after the reload.
            await page.EvaluateAsync("() => { window.__beforeRestart = true; }");
            menu = await OpenPowerMenuAsync(page);
            await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Restart" }).ClickAsync();
            dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Restart HackerOS" });
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Restart" }).ClickAsync();
            await page.WaitForFunctionAsync("() => window.__beforeRestart === undefined", null, new() { Timeout = 30000 });
            await SignInAgainAsync(page);
            output.WriteLine("[test] restart from the Power menu reloaded the page.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    internal static async Task<ILocator> OpenPowerMenuAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
        ILocator power = page.GetByRole(AriaRole.Button, new() { Name = "Power" });
        await Assertions.Expect(power).ToHaveAttributeAsync("aria-expanded", "false");
        await power.ClickAsync();
        await Assertions.Expect(power).ToHaveAttributeAsync("aria-expanded", "true");
        ILocator menu = page.GetByRole(AriaRole.Menu, new() { Name = "Power options" });
        await Assertions.Expect(menu.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" })).ToBeFocusedAsync();
        return menu;
    }

    internal static async Task SignInAgainAsync(IPage page)
    {
        ILocator passwordField = page.GetByLabel("Password", new() { Exact = true });
        await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
        await passwordField.FillAsync(E2ESupport.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
    }
}
