using System.Text;
using Microsoft.Playwright;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>Exercises the real pointer module without booting the OS or requiring a backend.</summary>
public sealed class WindowPointerGestureTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Release_never_adds_the_preview_to_the_committed_position(bool projectBeforeAcknowledgement, bool constrained)
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);
        await page.EvaluateAsync("""
            options => {
                delayMoveAcknowledgement = true;
                deferMoveProjection = !options.projectBeforeAcknowledgement;
                constrainedMove = options.constrained;
            }
            """, new { projectBeforeAcknowledgement, constrained });
        await page.Mouse.MoveAsync(80, 55);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(380, 255, new() { Steps = 6 });
        await page.WaitForFunctionAsync("document.querySelector('article').style.getPropertyValue('--window-drag-translation') !== ''");
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("typeof releaseMoveAcknowledgement === 'function'");

        // Sample actual layout over separate painted frames while the command's reply is held.
        // The old code renders x = 648.25 here when the base position has already been projected.
        float[] waiting = await SampleFramesAsync(page);
        Assert.All(waiting, x => Assert.Equal(348.25f, x));

        await page.EvaluateAsync("releaseMoveAcknowledgement()");
        await page.WaitForFunctionAsync("calls.some(c => c[0] === 'ReportGestureCompletedAsync')");
        float expected = constrained ? 148.25f : 348.25f;
        Assert.All(await SampleFramesAsync(page), x => Assert.Equal(expected, x));
        // A late OnAfterRender geometry projection must not cause a second visible change.
        await page.EvaluateAsync("projectMove?.()");
        Assert.All(await SampleFramesAsync(page), x => Assert.Equal(expected, x));
    }

    private static Task<float[]> SampleFramesAsync(IPage page) => page.EvaluateAsync<float[]>("""
        async () => {
            const positions = [];
            for (let i = 0; i < 4; i++) {
                await new Promise(requestAnimationFrame);
                positions.push(document.querySelector('article').getBoundingClientRect().x);
            }
            return positions;
        }
        """);

    [Fact]
    public async Task Repeated_fractional_moves_preview_locally_and_commit_only_on_release()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);
        List<string> errors = [];
        page.PageError += (_, error) => errors.Add(error);

        for (int gesture = 1; gesture <= 2; gesture++)
        {
            ILocator header = page.Locator("header");
            LocatorBoundingBoxResult box = (await header.BoundingBoxAsync())!;
            float x = box.X + 20;
            float y = box.Y + 10;
            string? before = await page.Locator("article").GetAttributeAsync("data-window-x");
            await page.Mouse.MoveAsync(x, y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(x + 30.5f, y + 20.25f, new() { Steps = 20 });
            await page.WaitForFunctionAsync("document.querySelector('article').style.getPropertyValue('--window-drag-translation') !== ''");
            LocatorBoundingBoxResult preview = (await header.BoundingBoxAsync())!;
            Assert.Equal(box.X + 30.5f, preview.X);
            Assert.Equal(box.Y + 20.25f, preview.Y);
            Assert.Equal(before, await page.Locator("article").GetAttributeAsync("data-window-x"));
            Assert.Equal(gesture - 1, await page.EvaluateAsync<int>("calls.filter(c => c[0] === 'ReportPointerDeltaAsync').length"));

            await page.Mouse.UpAsync();
            await page.WaitForFunctionAsync("count => calls.filter(c => c[0] === 'ReportGestureCompletedAsync').length === count", gesture);
            await page.WaitForFunctionAsync("document.querySelector('article').style.getPropertyValue('--window-drag-translation') === ''");
            Assert.Equal(48.25 + (gesture * 30.5), await page.EvaluateAsync<double>("Number(document.querySelector('article').dataset.windowX)"));
            Assert.Equal(42.5 + (gesture * 20.25), await page.EvaluateAsync<double>("Number(document.querySelector('article').dataset.windowY)"));
            Assert.Equal((float)(48.25 + (gesture * 30.5)), (await page.Locator("article").BoundingBoxAsync())!.X);
        }

        Assert.True(await page.EvaluateAsync<bool>("calls.filter(c => c[0] === 'ReportPointerDeltaAsync').every(c => c.slice(3).every(Number.isFinite))"));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Lost_capture_finishes_once_and_detaching_removes_an_active_gesture()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);
        await page.Mouse.MoveAsync(80, 55);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(100, 75);
        await page.EvaluateAsync("document.querySelector('header').releasePointerCapture(lastPointer)");
        await page.Mouse.UpAsync();
        await page.WaitForFunctionAsync("calls.filter(c => c[0] === 'ReportGestureCompletedAsync').length === 1");

        await page.Mouse.MoveAsync(100, 75);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(120, 95);
        await page.EvaluateAsync("gestureModule.detachWindowGestures(document.querySelector('#root'))");
        await page.Mouse.UpAsync();
        await page.Mouse.MoveAsync(140, 115);
        Assert.Equal(1, await page.EvaluateAsync<int>("calls.filter(c => c[0] === 'ReportGestureCompletedAsync').length"));
        Assert.Equal("", await page.Locator("article").EvaluateAsync<string>("e => e.style.getPropertyValue('--window-drag-translation')"));
    }

    [Fact]
    public async Task An_interop_failure_cleans_up_and_allows_the_next_drag()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);
        List<string> errors = [];
        page.PageError += (_, error) => errors.Add(error);
        await page.EvaluateAsync("failNextDelta = true");
        for (int gesture = 1; gesture <= 2; gesture++)
        {
            await page.Mouse.MoveAsync(80, 55);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(100, 75, new() { Steps = 4 });
            await page.Mouse.UpAsync();
            await page.WaitForFunctionAsync("count => calls.filter(c => c[0] === 'ReportPointerDeltaAsync').length === count", gesture);
            await page.WaitForFunctionAsync("document.querySelector('article').style.getPropertyValue('--window-drag-translation') === ''");
        }

        Assert.Equal(1, await page.EvaluateAsync<int>("calls.filter(c => c[0] === 'ReportGestureCompletedAsync').length"));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Slow_resize_keeps_one_inflight_sample_and_the_final_release_coordinates()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await PrepareAsync(browser);
        await page.EvaluateAsync("slowResize = true");
        LocatorBoundingBoxResult box = (await page.Locator(".resize").BoundingBoxAsync())!;
        float x = box.X + 2;
        float y = box.Y + 2;
        await page.Mouse.MoveAsync(x, y);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(x + 10, y + 10);
        await page.WaitForFunctionAsync("typeof releaseResize === 'function'");
        await page.Mouse.MoveAsync(x + 80, y + 40, new() { Steps = 40 });
        Assert.Equal(1, await page.EvaluateAsync<int>("calls.filter(c => c[0] === 'ReportPointerDeltaAsync').length"));
        await page.Mouse.UpAsync();
        await page.EvaluateAsync("slowResize = false; releaseResize()");
        await page.WaitForFunctionAsync("calls.filter(c => c[0] === 'ReportGestureCompletedAsync').length === 1");
        Assert.Equal(2, await page.EvaluateAsync<int>("calls.filter(c => c[0] === 'ReportPointerDeltaAsync').length"));
        Assert.Equal(720.75, await page.EvaluateAsync<double>("Number(document.querySelector('article').dataset.windowWidth)"));
        Assert.Equal(520.5, await page.EvaluateAsync<double>("Number(document.querySelector('article').dataset.windowHeight)"));
    }

    private static async Task<IPage> PrepareAsync(IBrowser browser)
    {
        IPage page = await browser.NewPageAsync(new() { Locale = "fr-FR", ViewportSize = new() { Width = 1400, Height = 900 } });
        string directory = E2ESupport.FindSolutionDirectory();
        string library = Path.Combine(directory, "Platform", "Kazo.Os.Windowing.Blazor");
        await page.SetContentAsync("""
            <article class="window-host" data-window-id="test" data-window-state="Normal"
                data-window-x="48.25" data-window-y="42.5" data-window-width="640.75" data-window-height="480.5">
                <div id="root"><header data-window-gesture="move">Test window</header>
                <span class="resize" data-window-gesture="resize" data-resize-edge="bottom-right"></span></div>
            </article>
            """);
        await page.AddStyleTagAsync(new() { Content = await File.ReadAllTextAsync(Path.Combine(library, "WindowHost.razor.css")) });
        await page.AddStyleTagAsync(new() { Content = "header {height: 35px; touch-action: none} .resize {position:absolute; bottom:0;right:0;width:12px;height:12px;touch-action:none}" });
        string module = Convert.ToBase64String(Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(Path.Combine(library, "WindowChrome.razor.js"))));
        string hostModule = Convert.ToBase64String(Encoding.UTF8.GetBytes(await File.ReadAllTextAsync(Path.Combine(library, "WindowHost.razor.js"))));
        await page.EvaluateAsync("""
            async modules => {
                window.gestureModule = await import('data:text/javascript;base64,' + modules.module);
                const hostModule = await import('data:text/javascript;base64,' + modules.hostModule);
                window.calls = [];
                window.slowResize = false;
                window.failNextDelta = false;
                window.delayMoveAcknowledgement = false;
                window.deferMoveProjection = false;
                window.constrainedMove = false;
                window.projectMove = null;
                const host = document.querySelector('article');
                host.style.setProperty('--window-x', host.dataset.windowX + 'px');
                host.style.setProperty('--window-y', host.dataset.windowY + 'px');
                host.style.setProperty('--window-width', host.dataset.windowWidth + 'px');
                host.style.setProperty('--window-height', host.dataset.windowHeight + 'px');
                document.querySelector('header').addEventListener('pointerdown', e => window.lastPointer = e.pointerId);
                gestureModule.attachWindowGestures(document.querySelector('#root'), {
                    async invokeMethodAsync(...args) {
                        calls.push(args);
                        if (args[0] === 'ReportGestureCompletedAsync' && args[1] === 'move') {
                            gestureModule.completeWindowMove(document.querySelector('#root'),
                                Number(host.dataset.windowX), Number(host.dataset.windowY));
                        }
                        if (args[0] !== 'ReportPointerDeltaAsync') return;
                        if (failNextDelta) {
                            failNextDelta = false;
                            throw new Error('simulated interop failure');
                        }
                        if (slowResize) await new Promise(resolve => window.releaseResize = resolve);
                        const [, mode, , dx, dy, x, y, width, height] = args;
                        if (mode === 'move') {
                            const finalX = constrainedMove ? 148.25 : x + dx;
                            const finalY = constrainedMove ? 142.5 : y + dy;
                            host.dataset.windowX = finalX;
                            host.dataset.windowY = finalY;
                            window.projectMove = () => hostModule.projectWindowGeometry(host, finalX, finalY, width, height, 1);
                            if (!deferMoveProjection) projectMove();
                            if (delayMoveAcknowledgement) await new Promise(resolve => window.releaseMoveAcknowledgement = resolve);
                        } else {
                            host.dataset.windowWidth = width + dx;
                            host.dataset.windowHeight = height + dy;
                        }
                    }
                });
            }
            """, new { module, hostModule });
        return page;
    }
}
