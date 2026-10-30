using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0201: « Switch user » of the Start menu's Power menu moves the session to the background
/// without closing its apps, and the sign-in screen opens. Signing in again to the same account
/// resumes the session: its window comes back with what was typed in it.
/// </summary>
public sealed class StartMenuSwitchUserTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Switch_user_keeps_the_session_open_and_signing_in_again_resumes_it()
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

            // An app with unsaved text: it must survive the switch.
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            ILocator editor = page.Locator("#editor-textarea");
            await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
            await editor.FillAsync("kept across the switch");

            ILocator menu = await StartMenuPowerTests.OpenPowerMenuAsync(page);
            await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Switch user" }).ClickAsync();

            // The sign-in screen covers the desktop; the account shows its open session.
            ILocator user = page.GetByLabel("Local user", new() { Exact = true });
            await user.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
            await Assertions.Expect(user.Locator("option")).ToContainTextAsync(["open session"]);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })).ToHaveCountAsync(0);
            await Assertions.Expect(editor).ToBeHiddenAsync();
            output.WriteLine("[test] switch user reached the sign-in screen with the session open.");

            await StartMenuPowerTests.SignInAgainAsync(page);

            // Resumed, not reopened: the same window, with its unsaved text.
            await Assertions.Expect(editor).ToBeVisibleAsync(new() { Timeout = 20000 });
            await Assertions.Expect(editor).ToHaveValueAsync("kept across the switch");
            output.WriteLine("[test] signing in again resumed the session and its window.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    [Fact]
    public async Task A_second_account_gets_its_own_desktop_and_switching_back_resumes_the_first()
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
            await AddAccountAsync(page, SecondLogin, "Second User", SecondPassword);

            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            ILocator editor = page.Locator("#editor-textarea");
            await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
            await editor.FillAsync("first session text");

            // The second account signs in: its desktop shows none of the first session's windows.
            await SwitchUserAsync(page);
            await SignInAsync(page, SecondLogin, SecondPassword);
            await Assertions.Expect(editor).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("article[data-window-id]:visible")).ToHaveCountAsync(0);
            output.WriteLine("[test] the second account has its own empty desktop.");

            // Back to the first account: its session resumes with its window and text.
            await SwitchUserAsync(page);
            await SignInAsync(page, E2ESupport.LoginName, E2ESupport.Password);
            await Assertions.Expect(editor).ToBeVisibleAsync(new() { Timeout = 20000 });
            await Assertions.Expect(editor).ToHaveValueAsync("first session text");

            // The sign-out dialog names the other open session.
            ILocator menu = await StartMenuPowerTests.OpenPowerMenuAsync(page);
            await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Sign out" }).ClickAsync();
            ILocator dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Sign out of HackerOS" });
            await Assertions.Expect(dialog).ToContainTextAsync("Other open sessions (1)");
            await Assertions.Expect(dialog).ToContainTextAsync("Second User");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            output.WriteLine("[test] switching back resumed the first session.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private const string SecondLogin = "second";
    private const string SecondPassword = "Second-passw0rd";

    /// <summary>Settings ▸ Users ▸ Add the account (ADR 0169), confirmed by the administrator's password.</summary>
    private async Task AddAccountAsync(IPage page, string login, string displayName, string password)
    {
        await E2ESupport.OpenAppAsync(page, "Settings", output);
        ILocator usersTab = page.Locator("#settings-nav-users");
        await usersTab.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await usersTab.ClickAsync();
        await page.GetByLabel("Login name (lowercase").FillAsync(login);
        await page.GetByLabel("Display name").FillAsync(displayName);
        await page.GetByLabel("Initial password").FillAsync(password);
        await page.GetByLabel("Confirm the new password").First.FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add the account" }).ClickAsync();

        ILocator authenticate = page.Locator(".aaa-authenticate");
        await authenticate.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        ILocator form = page.Locator("form:has(.aaa-authenticate)");
        await form.Locator("input[autocomplete='username']").FillAsync(E2ESupport.LoginName);
        await form.Locator("input[type='password']").FillAsync(E2ESupport.Password);
        await authenticate.ClickAsync();
        await Assertions.Expect(page.GetByText($"Account {login} added.")).ToBeVisibleAsync(new() { Timeout = 20000 });
        output.WriteLine($"[test] account '{login}' added.");
    }

    private static async Task SwitchUserAsync(IPage page)
    {
        ILocator menu = await StartMenuPowerTests.OpenPowerMenuAsync(page);
        await menu.GetByRole(AriaRole.Menuitem, new() { Name = "Switch user" }).ClickAsync();
        await page.GetByLabel("Local user", new() { Exact = true })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
    }

    private static async Task SignInAsync(IPage page, string login, string password)
    {
        await page.GetByLabel("Local user", new() { Exact = true }).SelectOptionAsync(login);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
    }
}
