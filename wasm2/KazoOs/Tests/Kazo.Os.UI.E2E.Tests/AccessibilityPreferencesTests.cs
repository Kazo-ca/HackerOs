using System.Diagnostics;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0105 section 4: the automated accessibility coverage of <c>v3/13</c>. Axe scans of the
/// surfaces <see cref="AccessibilityAndVisualCoverageTests"/> does not reach (Terminal, Browser,
/// Calculator, System Monitor, Theme Editor, Settings &gt; Backup and Settings &gt; Accessibility),
/// each once with the standard look and once with <c>highContrast = on</c>; a keyboard-only journey
/// through the launcher and the window commands with a focus that stays visible; and
/// <c>textScale = 2.0</c> without the desktop or the system apps' windows overflowing.
/// The preferences are set through Settings &gt; Accessibility, as a user would.
/// </summary>
public sealed class AccessibilityPreferencesTests(ITestOutputHelper output)
{
    private static readonly ViewportSize DesktopViewport = new() { Width = 1280, Height = 800 };

    /// <summary>The apps scanned, by launcher name; Settings' pages are scanned on their own.</summary>
    private static readonly string[] ScannedApps = ["Terminal", "Browser", "Calculator", "System Monitor", "Theme Editor"];

    [Fact]
    public async Task Every_listed_surface_has_no_serious_axe_violation_with_and_without_high_contrast()
    {
        await RunOnDesktopAsync(async (page, screenshots) =>
        {
            List<string> findings = [];

            await ScanEverySurfaceAsync(page, "standard", screenshots, findings);

            await OpenAccessibilityPageAsync(page);
            await ChooseAsync(page, "High contrast", "On");
            await page.Locator(".theme-scope[data-contrast='high']")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15000 });
            // The layer really repaints the desktop: the focus ring token is the high-contrast yellow.
            string focusRing = await page.EvaluateAsync<string>(
                "() => getComputedStyle(document.querySelector('.theme-scope')).getPropertyValue('--hos-focus-ring').trim()");
            Assert.Equal("#ffff00", focusRing);
            await CloseFocusedWindowAsync(page);

            await ScanEverySurfaceAsync(page, "high-contrast", screenshots, findings);

            if (findings.Count > 0)
            {
                output.WriteLine("[test] Findings:\n" + string.Join('\n', findings));
            }

            Assert.True(findings.Count == 0, $"{findings.Count} accessibility finding(s); see test output for detail.");
        });
    }

    [Fact]
    public async Task The_desktop_can_be_driven_with_the_keyboard_alone_and_the_focus_stays_visible()
    {
        await RunOnDesktopAsync(async (page, _) =>
        {
            ILocator windows = page.Locator("article.window-host");
            ILocator focused = page.Locator("article.window-host.is-focused");

            // 1. Tab to the launcher button, open it with Enter, search, pick with the arrows, launch with Enter.
            await TabToAsync(page, "App launcher");
            await AssertFocusVisibleAsync(page, "launcher button");
            await page.Keyboard.PressAsync("Enter");
            ILocator search = page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" });
            await Assertions.Expect(search).ToBeFocusedAsync(new() { Timeout = 15000 });
            await AssertFocusVisibleAsync(page, "launcher search");
            await LaunchFromSearchAsync(page, "Calculator");
            await Assertions.Expect(windows).ToHaveCountAsync(1, new() { Timeout = 15000 });

            await TabToAsync(page, "App launcher");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(search).ToBeFocusedAsync(new() { Timeout = 15000 });
            await LaunchFromSearchAsync(page, "Text Editor");
            await Assertions.Expect(windows).ToHaveCountAsync(2, new() { Timeout = 15000 });
            string second = (await focused.GetAttributeAsync("data-window-id"))!;

            // 2. Alt+` switches to the other window.
            await page.Keyboard.DownAsync("Alt");
            await page.Keyboard.PressAsync("Backquote");
            await Assertions.Expect(page.Locator(".window-switcher")).ToBeVisibleAsync();
            await page.Keyboard.UpAsync("Alt");
            await Assertions.Expect(focused).Not.ToHaveAttributeAsync("data-window-id", second, new() { Timeout = 15000 });
            await AssertFocusedWindowStandsOutAsync(page);

            // 3. Alt+F7 moves the focused window with the arrows; Enter keeps the new place.
            System.Text.Json.JsonElement before = await BoundsAsync(page);
            await page.Keyboard.PressAsync("Alt+F7");
            for (int step = 0; step < 5; step++)
            {
                await page.Keyboard.PressAsync("ArrowRight");
            }

            await page.Keyboard.PressAsync("Enter");
            await page.WaitForTimeoutAsync(300);
            System.Text.Json.JsonElement after = await BoundsAsync(page);
            Assert.True(
                after.GetProperty("left").GetDouble() > before.GetProperty("left").GetDouble(),
                $"Alt+F7 then ArrowRight did not move the window ({before} -> {after}).");

            // 4. Alt+Q closes it; the other window takes the focus, still visibly.
            await page.Keyboard.PressAsync("Alt+KeyQ");
            await Assertions.Expect(windows).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await Assertions.Expect(focused).ToHaveAttributeAsync("data-window-id", second, new() { Timeout = 15000 });
            await AssertFocusedWindowStandsOutAsync(page);
            output.WriteLine("[test] launcher, Alt+`, Alt+F7 and Alt+Q all worked from the keyboard.");
        });
    }

    [Fact]
    public async Task Text_at_twice_the_size_overflows_neither_the_desktop_nor_the_system_windows()
    {
        await RunOnDesktopAsync(async (page, screenshots) =>
        {
            await OpenAccessibilityPageAsync(page);
            await ChooseAsync(page, "Text size", "200%");
            await page.Locator(".theme-scope[data-text-scale='200']")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15000 });
            string rootFontSize = await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).fontSize");
            Assert.Equal("32px", rootFontSize);
            await CloseFocusedWindowAsync(page);

            List<string> findings = [];
            await CheckOverflowAsync(page, "desktop", findings);
            foreach (string app in new[] { "File Explorer", "Settings", "Terminal", "Calculator", "System Monitor", "Text Editor" })
            {
                await E2ESupport.OpenAppAsync(page, app, output);
                await page.Locator("article.window-host.is-focused")
                    .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
                await page.WaitForTimeoutAsync(500);
                await CheckOverflowAsync(page, app, findings);
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(screenshots, $"text-scale-200-{Slug(app)}.png"),
                    FullPage = true
                });
                await CloseFocusedWindowAsync(page);
            }

            if (findings.Count > 0)
            {
                output.WriteLine("[test] Findings:\n" + string.Join('\n', findings));
            }

            Assert.True(findings.Count == 0, $"{findings.Count} overflow finding(s); see test output for detail.");
        });
    }

    /// <summary>Boots the harness, signs in and runs <paramref name="body"/> on the desktop.</summary>
    private async Task RunOnDesktopAsync(Func<IPage, string, Task> body)
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);
        string screenshots = Path.Combine(solutionDirectory, "artifacts", "playwright", "accessibility");
        Directory.CreateDirectory(screenshots);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = DesktopViewport });
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await body(page, screenshots);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Scans each listed app, then Settings' Backup and Accessibility pages, closing each window after.</summary>
    private async Task ScanEverySurfaceAsync(IPage page, string look, string screenshots, List<string> findings)
    {
        foreach (string app in ScannedApps)
        {
            await E2ESupport.OpenAppAsync(page, app, output);
            await page.Locator("article.window-host.is-focused")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await page.WaitForTimeoutAsync(800); // let the window finish its mount and first data load
            await ScanAsync(page, $"{look}-{Slug(app)}", screenshots, findings);
            await CloseFocusedWindowAsync(page);
        }

        await E2ESupport.OpenAppAsync(page, "Settings", output);
        ILocator backupTab = page.GetByRole(AriaRole.Tab, new() { Name = "Backup" });
        await backupTab.DispatchEventAsync("click");
        await page.WaitForTimeoutAsync(500);
        await ScanAsync(page, $"{look}-settings-backup", screenshots, findings);
        await page.GetByRole(AriaRole.Tab, new() { Name = "Accessibility" }).DispatchEventAsync("click");
        await page.GetByRole(AriaRole.Radiogroup, new() { Name = "High contrast" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await ScanAsync(page, $"{look}-settings-accessibility", screenshots, findings);
        await CloseFocusedWindowAsync(page);
    }

    /// <summary>Opens Settings on its Accessibility page.</summary>
    private async Task OpenAccessibilityPageAsync(IPage page)
    {
        await E2ESupport.OpenAppAsync(page, "Settings", output);
        await page.GetByRole(AriaRole.Tab, new() { Name = "Accessibility" }).DispatchEventAsync("click");
        await page.GetByRole(AriaRole.Radiogroup, new() { Name = "High contrast" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
    }

    /// <summary>Picks one option of a radio group of Settings &gt; Accessibility and waits until it is saved.</summary>
    private static async Task ChooseAsync(IPage page, string group, string option)
    {
        ILocator radio = page.GetByRole(AriaRole.Radiogroup, new() { Name = group })
            .GetByRole(AriaRole.Radio, new() { Name = option, Exact = true });
        await radio.ClickAsync();
        await Assertions.Expect(radio).ToHaveAttributeAsync("aria-checked", "true", new() { Timeout = 15000 });
    }

    private static async Task CloseFocusedWindowAsync(IPage page)
    {
        ILocator windows = page.Locator("article.window-host");
        int count = await windows.CountAsync();
        await page.Locator("article.window-host.is-focused button.chrome-button-close").ClickAsync();
        await Assertions.Expect(windows).ToHaveCountAsync(count - 1, new() { Timeout = 15000 });
    }

    /// <summary>Runs an axe scan; a serious or critical violation is a finding (the rule of the existing scans).</summary>
    private async Task ScanAsync(IPage page, string surface, string screenshots, List<string> findings)
    {
        var result = await page.RunAxe();
        var blocking = result.Violations?
            .Where(item => string.Equals(item.Impact, "serious", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Impact, "critical", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (blocking.Length > 0)
        {
            findings.Add($"{surface}: serious/critical axe violations: {string.Join(", ", blocking.Select(item => item.Id))}");
            foreach (var violation in blocking)
            {
                foreach (var node in violation.Nodes ?? [])
                {
                    string summary = string.Join("; ", (node.All ?? []).Concat(node.Any ?? []).Concat(node.None ?? [])
                        .Select(check => check.Message));
                    output.WriteLine($"[test]   {surface} :: {violation.Id} :: {node.Target?.Selector} :: {node.Html} :: {summary}");
                }
            }
        }

        string path = Path.Combine(screenshots, $"{surface}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
        output.WriteLine($"[test] '{surface}': {blocking.Length} blocking axe finding(s); screenshot saved to {path}");
    }

    /// <summary>Presses Tab until the element named <paramref name="name"/> has the focus (keyboard only).</summary>
    private static async Task TabToAsync(IPage page, string name)
    {
        for (int press = 0; press < 60; press++)
        {
            string? label = await page.EvaluateAsync<string?>(
                "() => document.activeElement?.getAttribute('aria-label') ?? document.activeElement?.textContent?.trim() ?? null");
            if (string.Equals(label, name, StringComparison.Ordinal))
            {
                return;
            }

            await page.Keyboard.PressAsync("Tab");
        }

        throw new Xunit.Sdk.XunitException($"'{name}' never received the focus after 60 presses of Tab.");
    }

    /// <summary>Types the app's name in the launcher's search box, selects it with the arrows and launches it with Enter.</summary>
    private static async Task LaunchFromSearchAsync(IPage page, string app)
    {
        await page.Keyboard.TypeAsync(app);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Launch {app}", Exact = true })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.Keyboard.PressAsync("ArrowDown");
        await page.Keyboard.PressAsync("Enter");
    }

    /// <summary>
    /// The focused element shows a focus indicator while it matches :focus-visible: an outline or a
    /// box shadow on itself or, for a field drawn inside a box (the launcher search), on the box
    /// that rings it through :focus-within, at most three levels up.
    /// </summary>
    private static async Task AssertFocusVisibleAsync(IPage page, string what)
    {
        bool visible = await page.EvaluateAsync<bool>(@"() => {
            const element = document.activeElement;
            if (!element || element === document.body || !element.matches(':focus-visible')) return false;
            const ringed = node => {
                const style = getComputedStyle(node);
                const outline = style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0;
                return outline || style.boxShadow !== 'none';
            };
            let node = element;
            for (let level = 0; node && level < 4; level++, node = node.parentElement) {
                if (ringed(node)) return true;
            }
            return false;
        }");
        Assert.True(visible, $"The focus on the {what} is not visible.");
    }

    /// <summary>The focused window holds the keyboard focus and is drawn apart from the others (its focused border).</summary>
    private static async Task AssertFocusedWindowStandsOutAsync(IPage page)
    {
        string problem = await page.EvaluateAsync<string>(@"() => {
            const focused = document.querySelector('article.window-host.is-focused');
            const other = document.querySelector('article.window-host:not(.is-focused)');
            const active = document.activeElement;
            const describe = e => e ? e.tagName.toLowerCase() + (e.className ? '.' + String(e.className).trim().split(/\s+/).join('.') : '') : 'nothing';
            if (!focused) return 'no window is marked focused';
            if (!focused.contains(active)) return 'the keyboard focus is on ' + describe(active) + ', outside the focused window';
            if (!other) return '';
            const a = getComputedStyle(focused), b = getComputedStyle(other);
            return a.borderTopColor !== b.borderTopColor || a.boxShadow !== b.boxShadow || a.outlineStyle !== b.outlineStyle
                ? '' : 'the focused window is drawn like the others';
        }");
        Assert.True(problem.Length == 0, $"The focused window does not stand out: {problem}.");
    }

    private static Task<System.Text.Json.JsonElement> BoundsAsync(IPage page) =>
        page.EvaluateAsync<System.Text.Json.JsonElement>(
            "() => { const r = document.querySelector('article.window-host.is-focused').getBoundingClientRect(); return { left: r.left, top: r.top }; }");

    /// <summary>The page has no horizontal scroll and every window stays inside the viewport.</summary>
    private static async Task CheckOverflowAsync(IPage page, string surface, List<string> findings)
    {
        string? problem = await page.EvaluateAsync<string?>(@"() => {
            const root = document.documentElement;
            if (root.scrollWidth > root.clientWidth) return `page scrolls horizontally (${root.scrollWidth} > ${root.clientWidth})`;
            for (const win of document.querySelectorAll('article.window-host')) {
                const r = win.getBoundingClientRect();
                if (r.right > root.clientWidth + 1 || r.left < -1) return `window '${win.getAttribute('data-window-id')}' leaves the viewport (${r.left}..${r.right})`;
            }
            return null;
        }");
        if (problem is not null)
        {
            findings.Add($"{surface}: {problem}");
        }
    }

    private static string Slug(string name) => name.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant();
}
