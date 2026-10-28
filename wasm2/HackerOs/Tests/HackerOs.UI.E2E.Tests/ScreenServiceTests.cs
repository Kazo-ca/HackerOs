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

    [Fact]
    public async Task A_secondary_screen_shows_its_desktop_and_the_overlays_follow_the_screen_in_use()
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
            IPage screen = await RestoreScreenTwoAsync(page);

            // ADR 0178 §1: the screen's own desktop, with the wallpaper only and a window layer.
            ILocator desktop = screen.Locator(".secondary-screen .desktop-area");
            await Assertions.Expect(desktop).ToBeVisibleAsync(new() { Timeout = 20000 });
            await Assertions.Expect(screen.Locator(".secondary-screen .window-layer[data-display='2']")).ToHaveCountAsync(1);
            await Assertions.Expect(screen.Locator("[data-screen-overlay-slot][data-display='2']")).ToHaveCountAsync(1);
            await Assertions.Expect(screen.Locator(".secondary-screen-number")).ToHaveCountAsync(0);
            await Assertions.Expect(screen.Locator(".secondary-screen .background-layer")).ToHaveCountAsync(0);
            int[] fills = await desktop.EvaluateAsync<int[]>(
                "area => { const r = area.getBoundingClientRect(); return [Math.round(r.width), Math.round(r.height), innerWidth, innerHeight]; }");
            Assert.Equal(fills[2], fills[0]);
            Assert.Equal(fills[3], fills[1]);
            output.WriteLine($"[test] screen 2 shows its desktop, {fills[0]}×{fills[1]}.");

            // Two windows on screen 1, so that the switcher has somewhere to go.
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await E2ESupport.OpenAppAsync(page, "File Explorer", output);
            await Assertions.Expect(page.Locator("article.window-host")).ToHaveCountAsync(2, new() { Timeout = 15000 });

            // ADR 0179 §2: Alt+` pressed in screen 2 opens the switcher there, not on screen 1.
            await screen.BringToFrontAsync();
            await desktop.ClickAsync(new LocatorClickOptions { Position = new Position { X = 20, Y = 20 } });
            await screen.Keyboard.DownAsync("Alt");
            await screen.Keyboard.PressAsync("Backquote");
            await Assertions.Expect(screen.Locator(".window-switcher")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".window-switcher")).ToHaveCountAsync(0);
            await screen.Keyboard.UpAsync("Alt");
            await Assertions.Expect(screen.Locator(".window-switcher")).ToHaveCountAsync(0);
            output.WriteLine("[test] the switcher opened on screen 2, where the keys were pressed.");

            // Back on screen 1, the overlays come back with the next press.
            await page.BringToFrontAsync();
            await page.Locator(".desktop-workspace .desktop-area").First.ClickAsync(new LocatorClickOptions { Position = new Position { X = 5, Y = 5 } });
            await page.Keyboard.DownAsync("Alt");
            await page.Keyboard.PressAsync("Backquote");
            await Assertions.Expect(page.Locator(".window-switcher")).ToBeVisibleAsync();
            await Assertions.Expect(screen.Locator(".window-switcher")).ToHaveCountAsync(0);
            await page.Keyboard.UpAsync("Alt");
            output.WriteLine("[test] the switcher opened on screen 1 again.");

            // A screen closed while it holds the overlays gives them back to screen 1.
            await screen.BringToFrontAsync();
            await desktop.ClickAsync(new LocatorClickOptions { Position = new Position { X = 20, Y = 20 } });
            await screen.CloseAsync(new PageCloseOptions { RunBeforeUnload = true });
            await page.WaitForTimeoutAsync(1000);
            await page.BringToFrontAsync();
            await page.Keyboard.DownAsync("Alt");
            await page.Keyboard.PressAsync("Backquote");
            await Assertions.Expect(page.Locator(".window-switcher")).ToBeVisibleAsync();
            await page.Keyboard.UpAsync("Alt");
            output.WriteLine("[test] the overlays came back to screen 1 when screen 2 closed.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    [Fact]
    public async Task A_window_dragged_by_its_title_bar_onto_screen_2_moves_there()
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
            IPage screen = await RestoreScreenTwoAsync(page);
            await Assertions.Expect(screen.Locator(".secondary-screen .window-layer[data-display='2']")).ToHaveCountAsync(1, new() { Timeout = 20000 });
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            ILocator window = page.Locator("article.window-host").Last;
            await Assertions.Expect(window).ToBeVisibleAsync(new() { Timeout = 15000 });
            string? windowId = await window.GetAttributeAsync("data-window-id");

            // Each window learns where its page starts on the desktop from a pointer event (ADR 0183).
            const string Learn = "() => new Promise(done => addEventListener('pointermove', e => done([e.screenX - e.clientX, e.screenY - e.clientY]), { once: true, capture: true }))";
            Task<double[]> screenOriginTask = screen.EvaluateAsync<double[]>(Learn);
            await screen.Mouse.MoveAsync(300, 300);
            await screen.Mouse.MoveAsync(310, 310);
            double[] screenOrigin = await screenOriginTask;
            Task<double[]> mainOriginTask = page.EvaluateAsync<double[]>(Learn);
            await page.Mouse.MoveAsync(5, 5);
            await page.Mouse.MoveAsync(6, 6);
            double[] mainOrigin = await mainOriginTask;
            double[] mainSize = await page.EvaluateAsync<double[]>("() => [innerWidth, innerHeight]");
            output.WriteLine($"[test] page origins on the desktop: main {mainOrigin[0]},{mainOrigin[1]}; screen 2 {screenOrigin[0]},{screenOrigin[1]}.");

            // A point of screen 2 that the main tab does not cover (headless windows overlap):
            // above the main tab when screen 2's page starts higher, else to its left.
            double dropX = 300;
            double dropY = 300;
            if (screenOrigin[1] < mainOrigin[1])
            {
                dropY = (screenOrigin[1] + mainOrigin[1]) / 2 - screenOrigin[1];
            }
            else
            {
                dropX = (screenOrigin[0] + mainOrigin[0]) / 2 - screenOrigin[0];
            }

            double clientX = screenOrigin[0] + dropX - mainOrigin[0];
            double clientY = screenOrigin[1] + dropY - mainOrigin[1];
            Assert.True(clientX < 0 || clientY < 0 || clientX >= mainSize[0] || clientY >= mainSize[1],
                $"No point of screen 2 lies outside the main tab: main at {mainOrigin[0]},{mainOrigin[1]}, screen 2 at {screenOrigin[0]},{screenOrigin[1]}.");

            var title = (await window.Locator(".window-chrome h2").BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(title.X + 20, title.Y + title.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(title.X + 60, title.Y + title.Height / 2, new MouseMoveOptions { Steps = 4 });
            await page.Mouse.MoveAsync((float)clientX, (float)clientY, new MouseMoveOptions { Steps = 12 });
            // While the pointer is over screen 2, screen 2 shows where the window will land.
            await Assertions.Expect(screen.Locator(".window-drop-preview")).ToHaveCountAsync(1);
            await page.Mouse.UpAsync();

            await Assertions.Expect(screen.Locator($"article.window-host[data-window-id='{windowId}']")).ToBeVisibleAsync(new() { Timeout = 10000 });
            await Assertions.Expect(page.Locator($"article.window-host[data-window-id='{windowId}']")).ToHaveCountAsync(0);
            await Assertions.Expect(screen.Locator(".window-drop-preview")).ToHaveCountAsync(0);
            output.WriteLine("[test] the window dragged out of the main tab landed on screen 2.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    // Writes the file a previous session would have left (screen 2 open, at 40,40 and 900×700),
    // signs in again and restores the screen.
    private async Task<IPage> RestoreScreenTwoAsync(IPage page)
    {
        await E2ESupport.OpenAppAsync(page, "Terminal", output);
        ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
        await WaitForTextAsync(rows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
        await page.Locator("[data-terminal-panel] .xterm-screen").First.ClickAsync();
        await RunAsync(page, rows, "mkdir -p ~/.config/hackeros");
        await RunAsync(page, rows, "echo '[screens]' > ~/.config/hackeros/screens.config");
        await RunAsync(page, rows, "echo 'open = 2' >> ~/.config/hackeros/screens.config");
        await RunAsync(page, rows, "echo '2 = 40,40,900,700' >> ~/.config/hackeros/screens.config");
        await RunAsync(page, rows, "cat ~/.config/hackeros/screens.config");
        await WaitForTextAsync(rows, text => Count(text, "2 = 40,40,900,700") >= 2, "the screens file");

        await SignInAgainAsync(page);
        ILocator restore = page.Locator("#screens-restore");
        await restore.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
        IPage screen = await page.RunAndWaitForPopupAsync(() => restore.ClickAsync());
        E2ESupport.AttachDiagnostics(screen, output);
        await Assertions.Expect(screen.Locator(".secondary-screen[data-display='2']")).ToBeVisibleAsync(new() { Timeout = 20000 });
        return screen;
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
