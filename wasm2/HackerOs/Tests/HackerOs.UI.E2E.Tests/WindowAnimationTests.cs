using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0198 in a real browser: windows animate when they open, minimize, restore, maximize and
/// close, always end fully drawn, and the close animation is an inert copy that leaves the page.
/// With the browser asking for less motion, nothing is animated.
/// </summary>
public sealed class WindowAnimationTests(ITestOutputHelper output)
{
    // Records the window animations and the close copies, from before the desktop starts.
    private const string RecorderScript = """
        (() => {
            window.__hosAnimations = [];
            window.__hosGhosts = [];
            const animate = Element.prototype.animate;
            Element.prototype.animate = function (...args) {
                if (this.classList.contains('window-host')) {
                    window.__hosAnimations.push(this.classList.contains('is-closing-ghost') ? 'ghost' : 'window');
                }
                return animate.apply(this, args);
            };
            new MutationObserver(records => {
                for (const record of records) {
                    for (const node of record.addedNodes) {
                        if (node.nodeType === 1 && node.classList.contains('is-closing-ghost')) {
                            window.__hosGhosts.push({
                                tag: node.tagName,
                                ariaHidden: node.getAttribute('aria-hidden'),
                                inert: node.inert,
                                windowId: node.getAttribute('data-window-id'),
                                ids: node.querySelectorAll('[id]').length
                            });
                        }
                    }
                }
            }).observe(document, { childList: true, subtree: true });
        })();
        """;

    [Fact]
    public async Task Windows_animate_and_always_end_fully_drawn()
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
            await using IBrowserContext context = await browser.NewContextAsync(
                new() { ReducedMotion = ReducedMotion.NoPreference });
            await context.AddInitScriptAsync(RecorderScript);
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            ILocator window = page.Locator("article.window-host");
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await Assertions.Expect(window).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await ExpectFullyDrawnAsync(window);
            Assert.Contains("window", await AnimationsAsync(page));
            output.WriteLine("[test] The window opened with an animation and ended fully drawn.");

            string windowId = (await window.GetAttributeAsync("data-window-id"))!;
            await window.Locator(".chrome-button-minimize").ClickAsync();
            await Assertions.Expect(window).ToBeHiddenAsync(new() { Timeout = 5000 });

            await page.Locator($"[data-taskbar-window-id='{windowId}']").First.ClickAsync();
            await Assertions.Expect(window).ToBeVisibleAsync();
            await ExpectFullyDrawnAsync(window);
            output.WriteLine("[test] Minimize into the taskbar then restore ended fully drawn.");

            await window.Locator(".chrome-button-maximize").ClickAsync();
            await Assertions.Expect(window).ToHaveAttributeAsync("data-window-state", "Maximized");
            await ExpectFullyDrawnAsync(window);

            await window.Locator(".chrome-button-close").ClickAsync();
            await Assertions.Expect(window).ToHaveCountAsync(0, new() { Timeout = 15000 });
            await Assertions.Expect(page.Locator(".is-closing-ghost")).ToHaveCountAsync(0, new() { Timeout = 5000 });
            JsonElement ghosts = await page.EvaluateAsync<JsonElement>("window.__hosGhosts");
            Assert.Equal(1, ghosts.GetArrayLength());
            JsonElement ghost = ghosts[0];
            Assert.Equal("DIV", ghost.GetProperty("tag").GetString());
            Assert.Equal("true", ghost.GetProperty("ariaHidden").GetString());
            Assert.True(ghost.GetProperty("inert").GetBoolean());
            Assert.Equal(JsonValueKind.Null, ghost.GetProperty("windowId").ValueKind);
            Assert.Equal(0, ghost.GetProperty("ids").GetInt32());
            output.WriteLine("[test] Close faded an inert copy that then left the page.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    [Fact]
    public async Task Nothing_is_animated_when_the_browser_asks_for_less_motion()
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
            await using IBrowserContext context = await browser.NewContextAsync(
                new() { ReducedMotion = ReducedMotion.Reduce });
            await context.AddInitScriptAsync(RecorderScript);
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            ILocator window = page.Locator("article.window-host");
            await E2ESupport.OpenAppAsync(page, "Text Editor", output);
            await Assertions.Expect(window).ToHaveCountAsync(1, new() { Timeout = 15000 });
            await ExpectFullyDrawnAsync(window);

            string windowId = (await window.GetAttributeAsync("data-window-id"))!;
            await window.Locator(".chrome-button-minimize").ClickAsync();
            await Assertions.Expect(window).ToBeHiddenAsync(new() { Timeout = 5000 });
            await page.Locator($"[data-taskbar-window-id='{windowId}']").First.ClickAsync();
            await ExpectFullyDrawnAsync(window);

            await window.Locator(".chrome-button-close").ClickAsync();
            await Assertions.Expect(window).ToHaveCountAsync(0, new() { Timeout = 15000 });

            Assert.Empty(await AnimationsAsync(page));
            Assert.Equal(0, (await page.EvaluateAsync<JsonElement>("window.__hosGhosts")).GetArrayLength());
            output.WriteLine("[test] Reduced motion: no window animation, no close copy.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    private static async Task<string[]> AnimationsAsync(IPage page) =>
        await page.EvaluateAsync<string[]>("window.__hosAnimations");

    // No running animation, no leftover transform, fully opaque: a window never stays mid-animation.
    private static async Task ExpectFullyDrawnAsync(ILocator window)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string state = string.Empty;
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            state = await window.EvaluateAsync<string>("""
                el => {
                    const style = getComputedStyle(el);
                    const running = el.getAnimations().filter(a => a.playState === 'running').length;
                    return `${style.opacity}|${style.transform}|${running}|${el.classList.contains('is-opening')}`;
                }
                """);
            if (state == "1|none|0|false")
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The window did not end fully drawn: {state}");
    }
}
