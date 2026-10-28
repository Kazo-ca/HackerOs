using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// The screen service (ADR 0178 §4, ADR 0180) in a real Chromium: a screen listed in
/// <c>~/.config/hackeros/screens.config</c> is offered after a reload, « Restore screens » opens it
/// as a second window that shows screen 2, and a screen the user closes is not offered again.
/// Headless Chromium has no second monitor: the window opens at its remembered place (fallback).
/// </summary>
public sealed class ScreenServiceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_screen_of_the_last_session_is_restored_on_click_and_forgotten_once_closed()
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
            await Assertions.Expect(page.Locator("#screens-restore")).ToHaveCountAsync(0);

            // The file a previous session would have left: screen 2 open, at 40,40 and 900×700.
            await E2ESupport.OpenAppAsync(page, "Terminal", output);
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            await WaitForTextAsync(rows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
            await page.Locator("[data-terminal-panel] .xterm-screen").First.ClickAsync();
            await RunAsync(page, rows, "mkdir -p ~/.config/hackeros");
            await RunAsync(page, rows, "echo '[screens]' > ~/.config/hackeros/screens.config");
            await RunAsync(page, rows, "echo 'open = 2' >> ~/.config/hackeros/screens.config");
            await RunAsync(page, rows, "echo '2 = 40,40,900,700' >> ~/.config/hackeros/screens.config");
            await RunAsync(page, rows, "cat ~/.config/hackeros/screens.config");
            // Typed once in the command and once in cat's output.
            await WaitForTextAsync(rows, text => Count(text, "2 = 40,40,900,700") >= 2, "the screens file");

            await SignInAgainAsync(page);
            ILocator restore = page.Locator("#screens-restore");
            await restore.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
            output.WriteLine("[test] the screen of the last session is offered.");

            IPage screen = await page.RunAndWaitForPopupAsync(() => restore.ClickAsync());
            E2ESupport.AttachDiagnostics(screen, output);
            await Assertions.Expect(screen.Locator(".secondary-screen[data-display='2']")).ToBeVisibleAsync(new() { Timeout = 20000 });
            await Assertions.Expect(screen).ToHaveTitleAsync("HackerOS — Screen 2");
            await Assertions.Expect(screen.Locator(".secondary-screen-fullscreen")).ToBeVisibleAsync();
            await Assertions.Expect(restore).ToHaveCountAsync(0);
            int[] size = await screen.EvaluateAsync<int[]>("() => [innerWidth, innerHeight]");
            output.WriteLine($"[test] screen 2 opened, {size[0]}×{size[1]}.");

            // Closed by hand: the OS forgets it, so the next session offers nothing.
            await screen.CloseAsync(new PageCloseOptions { RunBeforeUnload = true });
            await page.WaitForTimeoutAsync(1500);
            await SignInAgainAsync(page);
            await page.WaitForTimeoutAsync(2000);
            await Assertions.Expect(page.Locator("#screens-restore")).ToHaveCountAsync(0);
            output.WriteLine("[test] a screen closed by the user is not offered again.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    // Types one command and waits for the next prompt: keys typed while a command runs are lost.
    private async Task RunAsync(IPage page, ILocator rows, string command)
    {
        int prompts = Count(await rows.InnerTextAsync(), ":~$");
        await page.Keyboard.TypeAsync(command);
        await page.Keyboard.PressAsync("Enter");
        await WaitForTextAsync(rows, text => Count(text, ":~$") > prompts, $"the prompt after '{command}'");
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static async Task SignInAgainAsync(IPage page)
    {
        await page.ReloadAsync();
        ILocator password = page.GetByLabel("Password", new() { Exact = true });
        await password.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
        await password.FillAsync(E2ESupport.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start session", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
    }

    /// <summary>Polls the terminal's rendered rows until <paramref name="done"/> holds.</summary>
    private async Task WaitForTextAsync(ILocator rows, Func<string, bool> done, string what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string text = string.Empty;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            text = (await rows.InnerTextAsync()).Replace(' ', ' ').Replace("\n", string.Empty, StringComparison.Ordinal);
            if (done(text))
            {
                return;
            }

            await Task.Delay(200);
        }

        output.WriteLine(text);
        throw new TimeoutException($"The terminal never showed {what}.");
    }
}
