using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0120: <c>passwd</c> in the terminal reads the passwords with echo off (none of them is drawn),
/// changes the account's password, and the next sign-in takes the new one only.
/// </summary>
public sealed class PasswdTerminalTests(ITestOutputHelper output)
{
    private const string NewPassword = "n3w-Passw0rd-e2e";

    [Fact]
    public async Task Passwd_hides_what_is_typed_and_the_next_sign_in_needs_the_new_password()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            await E2ESupport.OpenAppAsync(page, "Terminal", output);
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTextAsync(rows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
            await screen.ClickAsync();
            await page.Keyboard.TypeAsync("passwd");
            await page.Keyboard.PressAsync("Enter");

            await WaitForTextAsync(rows, text => text.Contains("Current password:", StringComparison.Ordinal), "the current password prompt");
            await page.Keyboard.TypeAsync(E2ESupport.Password);
            await page.Keyboard.PressAsync("Enter");
            await WaitForTextAsync(rows, text => text.Contains("New password:", StringComparison.Ordinal), "the new password prompt");
            await page.Keyboard.TypeAsync(NewPassword);
            await page.Keyboard.PressAsync("Enter");
            await WaitForTextAsync(rows, text => text.Contains("Retype new password:", StringComparison.Ordinal), "the retype prompt");
            await page.Keyboard.TypeAsync(NewPassword);
            await page.Keyboard.PressAsync("Enter");

            string text = await WaitForTextAsync(
                rows, t => t.Contains("passwd: password updated successfully", StringComparison.Ordinal), "the success line");
            Assert.DoesNotContain(E2ESupport.Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain(NewPassword, text, StringComparison.Ordinal);
            output.WriteLine("[test] passwd changed the password; nothing typed was echoed.");

            // The old password no longer opens the session; the new one does.
            await page.ReloadAsync();
            ILocator password = page.GetByLabel("Password", new() { Exact = true });
            await password.FillAsync(E2ESupport.Password, new() { Timeout = 60000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session", Exact = true }).ClickAsync();
            await page.GetByText("The local credentials were not accepted.").WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });

            await password.FillAsync(NewPassword);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
            output.WriteLine("[test] the old password is refused and the new one signs in.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Polls the terminal's rendered rows until <paramref name="done"/> holds.</summary>
    private async Task<string> WaitForTextAsync(ILocator rows, Func<string, bool> done, string what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string text = string.Empty;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            // Long lines wrap at the terminal's width: the rows are read as one text.
            text = (await rows.InnerTextAsync()).Replace(' ', ' ').Replace("\n", string.Empty, StringComparison.Ordinal);
            if (done(text))
            {
                return text;
            }

            await Task.Delay(200);
        }

        output.WriteLine(text);
        throw new TimeoutException($"The terminal never showed {what}.");
    }
}
