using System.Text;
using Microsoft.Playwright;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0234 §5 (issue #378): a None window is transparent and lets clicks through where its app draws
/// nothing; what the app draws takes the pointer, and <c>data-kazo-window-drag</c> moves the window.
/// Runs the real WindowHost and DesktopArea styles and the real pointer module in Chromium, without
/// booting the OS.
/// </summary>
public sealed class WindowFramelessTransparencyTests
{
    [Fact]
    public async Task Clicks_go_through_the_empty_areas_and_reach_what_the_app_draws()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);

        // Empty area of the window, over the desktop icon: the icon receives the click.
        Assert.Equal("desktop-icon", await HitAsync(page, 300, 300));
        await page.Mouse.ClickAsync(300, 300);
        Assert.Equal("desktop-icon", await page.EvaluateAsync<string>("lastClick"));

        // The app's drawing (a direct child of the client area) and a marked element inside a
        // wrapper the app made transparent both take the pointer.
        Assert.Equal("skin", await HitAsync(page, 120, 120));
        Assert.Equal("hit", await HitAsync(page, 560, 120));
        Assert.Equal("SECTION.background-layer interactive", await HitAsync(page, 560, 300));

        // The surface has no background, border or shadow.
        string[] surface = await page.EvaluateAsync<string[]>("""
            () => {
                const style = getComputedStyle(document.querySelector('article'));
                return [style.backgroundColor, style.boxShadow, style.borderTopStyle];
            }
            """);
        Assert.Equal(["rgba(0, 0, 0, 0)", "none", "none"], surface);
    }

    [Fact]
    public async Task A_normal_window_still_takes_every_click()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);
        await page.EvaluateAsync("document.querySelector('article').classList.remove('is-frameless')");

        Assert.NotEqual("desktop-icon", await HitAsync(page, 300, 300));
    }

    [Fact]
    public async Task A_drag_area_of_the_app_moves_the_window_but_a_field_inside_it_does_not()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);

        await page.Mouse.MoveAsync(200, 160);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(260, 200, new() { Steps = 6 });
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("calls.some(c => c[0] === 'ReportGestureCompletedAsync')");
        Assert.True(await page.EvaluateAsync<bool>("calls.some(c => c[0] === 'ReportPointerDeltaAsync' && c[1] === 'move')"));

        await page.EvaluateAsync("calls.length = 0");
        LocatorBoundingBoxResult field = (await page.Locator("#field").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(field.X + 5, field.Y + 5);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(field.X + 55, field.Y + 45, new() { Steps = 6 });
        await page.Mouse.UpAsync();
        Assert.Equal(0, await page.EvaluateAsync<int>("calls.length"));
    }

    private static Task<string> HitAsync(IPage page, int x, int y) =>
        page.EvaluateAsync<string>("p => { const e = document.elementFromPoint(p.x, p.y); return e ? (e.id || (e.tagName + '.' + e.className)) : ''; }", new { x, y });

    private static async Task<IPage> PrepareAsync(IBrowser browser)
    {
        IPage page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1000, Height = 700 } });
        string library = Path.Combine(E2ESupport.FindSolutionDirectory(), "Platform", "Kazo.Os.Windowing.Blazor");
        await page.SetContentAsync("""
            <main class="desktop-area">
              <section class="background-layer interactive">
                <button id="desktop-icon" onclick="window.lastClick = this.id">Icon</button>
              </section>
              <section class="window-layer pass-through">
                <article class="window-host is-frameless" data-window-id="w1" data-window-state="Normal"
                         data-window-x="40" data-window-y="40" data-window-width="640" data-window-height="400">
                  <div id="root" class="chrome-interaction-layer"></div>
                  <div class="window-content" id="content">
                    <div id="skin" data-kazo-window-drag>
                      Skin <input id="field" />
                    </div>
                    <div id="wrapper"><span id="hit" data-kazo-window-hit>Button</span></div>
                  </div>
                </article>
              </section>
            </main>
            """);
        // Scoped CSS without its build step: the ::deep markers only matter to the scoping.
        foreach (string file in new[] { "DesktopArea.razor.css", "WindowHost.razor.css" })
        {
            string css = (await File.ReadAllTextAsync(Path.Combine(library, file))).Replace("::deep ", string.Empty, StringComparison.Ordinal);
            await page.AddStyleTagAsync(new() { Content = css });
        }

        await page.AddStyleTagAsync(new() { Content = """
            body { margin: 0; }
            .desktop-area { position: fixed; inset: 0; }
            .background-layer { position: absolute; inset: 0; }
            #desktop-icon { position: absolute; left: 250px; top: 250px; width: 120px; height: 120px; }
            #skin { position: absolute; left: 20px; top: 20px; width: 300px; height: 120px; background: #335; touch-action: none; }
            #field { width: 80px; }
            #wrapper { position: absolute; left: 480px; top: 40px; width: 200px; height: 300px; pointer-events: none; }
            #hit { display: block; width: 120px; height: 60px; background: #533; }
            .window-content { position: relative; }
            """ });
        string module = Convert.ToBase64String(Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(Path.Combine(library, "WindowChrome.razor.js"))));
        await page.EvaluateAsync("""
            async module => {
                const gestures = await import('data:text/javascript;base64,' + module);
                window.calls = [];
                const host = document.querySelector('article');
                for (const name of ['x', 'y', 'width', 'height']) {
                    host.style.setProperty('--window-' + name, host.dataset['window' + name[0].toUpperCase() + name.slice(1)] + 'px');
                }
                gestures.attachWindowGestures(document.querySelector('#root'), {
                    async invokeMethodAsync(...args) {
                        calls.push(args);
                        if (args[0] === 'ReportGestureCompletedAsync' && args[1] === 'move') {
                            gestures.completeWindowMove(document.querySelector('#root'), 40, 40);
                        }
                    }
                });
            }
            """, module);
        return page;
    }
}
