using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// The screen service (ADR 0178 §4, ADR 0180) in a real Chromium: a screen listed in
/// <c>~/.config/hackeros/screens.config</c> is offered after a reload, « Restore screens » opens it
/// as a second window that shows screen 2 with its taskbar (ADR 0179 §1), and a screen the user closes is not offered again.
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
            // The desktop fills the window above the screen's taskbar (ADR 0179 §1).
            int taskbarHeight = await screen.Locator(".secondary-screen footer.taskbar-container").EvaluateAsync<int>("bar => Math.round(bar.getBoundingClientRect().height)");
            Assert.Equal(fills[2], fills[0]);
            Assert.InRange(fills[1] + taskbarHeight, fills[3] - 1, fills[3] + 1);
            output.WriteLine($"[test] screen 2 shows its desktop, {fills[0]}×{fills[1]}, above a {taskbarHeight}px taskbar.");

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
    public async Task The_other_screen_menus_identify_the_screens_and_move_a_window_between_them()
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
            List<string> errors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            IPage screen = await RestoreScreenTwoAsync(page);
            await page.BringToFrontAsync();
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            ILocator editor = page.Locator("article.window-host.is-focused");
            await Assertions.Expect(editor).ToBeVisibleAsync();

            // ADR 0179 §4: right click on Maximize opens snap, full screen and « Other screen ».
            await editor.Locator(".chrome-button-maximize").ClickAsync(new() { Button = MouseButton.Right });
            ILocator maximizeMenu = page.Locator("[data-context-menu='window-maximize-menu'][data-menu-level='0']");
            await Assertions.Expect(maximizeMenu).ToBeVisibleAsync();
            Assert.Equal(
                ["Snap left", "Snap right", "Full screen (Linux keys)", "Other screen"],
                await maximizeMenu.Locator(".hack-context-menu-title").AllInnerTextsAsync());
            await page.Keyboard.PressAsync("Escape");
            await Assertions.Expect(maximizeMenu).ToHaveCountAsync(0);

            // ADR 0179 §5-6: the window menu's submenu; hovering « Screen 2 » shows each other screen its number.
            await editor.Locator(".chrome-button-menu").ClickAsync(new() { Button = MouseButton.Right });
            ILocator windowMenu = page.Locator("[data-context-menu='window-menu'][data-menu-level='0']");
            await Assertions.Expect(windowMenu).ToBeVisibleAsync();
            await windowMenu.Locator("[data-menu-item='window.other-screen']").HoverAsync();
            ILocator screens = page.Locator("[data-context-menu='window-menu'][data-menu-level='1']");
            await Assertions.Expect(screens).ToBeVisibleAsync();
            Assert.Equal(
                ["Screen 1", "Screen 2", "New screen"],
                await screens.Locator(".hack-context-menu-title").AllInnerTextsAsync());
            await Assertions.Expect(screens.Locator("[data-menu-item='window.screen.1']")).ToHaveAttributeAsync("aria-disabled", "true");
            await screens.Locator("[data-menu-item='window.screen.2']").HoverAsync();
            ILocator veil = screen.Locator(".screen-identify-veil[data-screen-identify='2']");
            await Assertions.Expect(veil).ToBeVisibleAsync();
            await Assertions.Expect(veil).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("is-target"));
            await Assertions.Expect(veil.Locator(".screen-identify-number")).ToHaveTextAsync("2");
            Assert.Equal(0.33, await veil.EvaluateAsync<double>("v => Number(getComputedStyle(v).backgroundColor.match(/[\\d.]+\\)$/)[0].replace(')', ''))"), 2);
            // The screen that shows the menu keeps it usable: no veil there.
            await Assertions.Expect(page.Locator(".screen-identify-veil")).ToBeHiddenAsync();
            output.WriteLine("[test] hovering Screen 2 shows the number on screen 2 only.");

            // The veil goes away when the pointer passes over it, and when the pointer leaves the entry.
            await screen.Mouse.MoveAsync(200, 200);
            await screen.Mouse.MoveAsync(210, 210);
            await Assertions.Expect(veil).ToHaveCountAsync(0);
            await screens.Locator("[data-menu-item='window.new-screen']").HoverAsync();
            await screens.Locator("[data-menu-item='window.screen.2']").HoverAsync();
            await Assertions.Expect(veil).ToBeVisibleAsync();
            await windowMenu.Locator("[data-menu-item='window.close']").HoverAsync();
            await Assertions.Expect(veil).ToHaveCountAsync(0);
            output.WriteLine("[test] the veil leaves with the pointer.");

            // Choosing « Screen 2 » moves the window there.
            await windowMenu.Locator("[data-menu-item='window.other-screen']").HoverAsync();
            await screens.Locator("[data-menu-item='window.screen.2']").ClickAsync();
            await Assertions.Expect(page.Locator("[data-context-menu]")).ToHaveCountAsync(0);
            await Assertions.Expect(screen.Locator(".window-layer[data-display='2'] article.window-host")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            await Assertions.Expect(screen.Locator(".screen-identify-veil")).ToHaveCountAsync(0);
            output.WriteLine("[test] the window moved to screen 2.");

            // ADR 0178 §6: the fallback shortcut moves the active window to the previous screen.
            await screen.BringToFrontAsync();
            ILocator moved = screen.Locator(".window-layer[data-display='2'] article.window-host");
            await moved.Locator("header h2").ClickAsync();
            await screen.Keyboard.PressAsync("Control+Alt+Shift+ArrowLeft");
            await Assertions.Expect(screen.Locator(".window-layer[data-display='2'] article.window-host")).ToHaveCountAsync(0, new() { Timeout = 10000 });
            await Assertions.Expect(page.Locator(".desktop-workspace article.window-host")).ToHaveCountAsync(1);
            output.WriteLine("[test] the shortcut brought the window back to screen 1.");

            // « New screen » opens screen 3 during the click and moves the window there.
            await page.BringToFrontAsync();
            await editor.Locator(".chrome-button-menu").ClickAsync(new() { Button = MouseButton.Right });
            await windowMenu.Locator("[data-menu-item='window.other-screen']").HoverAsync();
            IPage third = await page.RunAndWaitForPopupAsync(() => screens.Locator("[data-menu-item='window.new-screen']").ClickAsync());
            E2ESupport.AttachDiagnostics(third, output);
            await Assertions.Expect(third.Locator(".secondary-screen[data-display='3']")).ToBeVisibleAsync(new() { Timeout = 20000 });
            await Assertions.Expect(third.Locator(".window-layer[data-display='3'] article.window-host")).ToHaveCountAsync(1, new() { Timeout = 10000 });
            output.WriteLine("[test] New screen opened screen 3 with the window.");
            Assert.Empty(errors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    [Fact]
    public async Task Each_screen_has_its_taskbar_and_start_menu_and_screen_1_can_list_only_its_windows()
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

            // ADR 0179 §1: screen 2 has its taskbar, under its desktop, which leaves it out of the work area.
            ILocator taskbar = screen.Locator(".secondary-screen footer.taskbar-container");
            await Assertions.Expect(taskbar).ToBeVisibleAsync(new() { Timeout = 20000 });
            await Assertions.Expect(taskbar.Locator(".taskbar-clock")).ToBeVisibleAsync();
            await Assertions.Expect(taskbar.Locator(".taskbar-notification-area")).ToHaveCountAsync(0);
            await Assertions.Expect(taskbar.Locator(".logout-trigger")).ToHaveCountAsync(0);
            int[] layout = await screen.EvaluateAsync<int[]>(
                """
                () => {
                    const desktop = document.querySelector('.secondary-screen .desktop-area').getBoundingClientRect();
                    const bar = document.querySelector('.secondary-screen footer.taskbar-container').getBoundingClientRect();
                    return [Math.round(desktop.bottom), Math.round(bar.top), Math.round(bar.bottom), innerHeight];
                }
                """);
            Assert.True(layout[0] <= layout[1] + 1, $"desktop bottom {layout[0]}, taskbar top {layout[1]}");
            Assert.Equal(layout[3], layout[2]);
            output.WriteLine($"[test] screen 2 has its taskbar at {layout[1]}..{layout[2]} of {layout[3]}.");

            // The Start menu of screen 2 shows on screen 2, and its app opens on screen 2.
            await screen.BringToFrontAsync();
            await E2ESupport.OpenAppAsync(screen, "Text Editor", output);
            await Assertions.Expect(page.Locator(".launcher-popover")).ToHaveCountAsync(0);
            await Assertions.Expect(screen.Locator(".window-layer[data-display='2'] article.window-host")).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(taskbar.Locator(".taskbar-item")).ToHaveCountAsync(1);

            // Screen 1's Start menu opens on screen 1.
            await page.BringToFrontAsync();
            await E2ESupport.OpenAppAsync(page, "Terminal", output);
            await Assertions.Expect(screen.Locator(".launcher-popover")).ToHaveCountAsync(0);
            ILocator primaryItems = page.Locator(".desktop-shell > footer.taskbar-container .taskbar-item");
            await Assertions.Expect(page.Locator(".desktop-workspace article.window-host")).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(taskbar.Locator(".taskbar-item")).ToHaveCountAsync(1);

            // primary-taskbar = all (default): screen 1's taskbar lists both windows.
            await Assertions.Expect(primaryItems).ToHaveCountAsync(2);
            output.WriteLine("[test] screen 1 lists every window, screen 2 its own.");

            // primary-taskbar = own: screen 1's taskbar keeps only its terminal, as soon as the file changes.
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            await WaitForTextAsync(rows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
            await page.Locator("[data-terminal-panel] .xterm-screen").First.ClickAsync();
            await RunAsync(page, rows, "echo '[taskbar]' >> ~/.config/hackeros/screens.config");
            await RunAsync(page, rows, "echo 'primary-taskbar = own' >> ~/.config/hackeros/screens.config");
            await Assertions.Expect(primaryItems).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(primaryItems.First).ToContainTextAsync("Terminal");
            await Assertions.Expect(taskbar.Locator(".taskbar-item")).ToHaveCountAsync(1);
            output.WriteLine("[test] with primary-taskbar = own, screen 1 lists only its window.");
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
