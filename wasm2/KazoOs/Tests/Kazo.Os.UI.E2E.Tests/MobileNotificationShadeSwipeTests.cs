using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

public sealed class MobileNotificationShadeSwipeTests(ITestOutputHelper output)
{
    /// <summary>
    /// End-to-end proof that the Mobile notification shade actually opens from a pointer swipe
    /// down past the open threshold (<c>mobileGestures.js</c>'s <c>attachMobileGestures</c>),
    /// not only from the handle's click fallback — <c>PlatformShellSwitchTests</c> only exercises
    /// the click path.
    /// </summary>
    [Fact]
    public async Task Swiping_down_from_the_handle_opens_the_shade()
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

            await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
            ILocator clockDialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await clockDialog.GetByRole(AriaRole.Radio, new() { Name = "Mobile" }).ClickAsync();

            ILocator shadeHandle = page.GetByRole(AriaRole.Button, new() { Name = "Notifications, calendar, and platform mode" });
            await shadeHandle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10000 });
            // The gesture listener attaches in OnAfterRenderAsync, slightly after the handle becomes
            // visible -- wait for the JS module's own marker rather than racing it.
            await Assertions.Expect(shadeHandle).ToHaveAttributeAsync("data-swipe-gesture-attached", "true", new() { Timeout = 5000 });
            output.WriteLine("[test] Mobile shell reached, swipe gesture listener attached.");

            ILocator shadePanel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(shadePanel).ToHaveCountAsync(0);

            // Neither page.Mouse's OS-level input simulation nor Locator.DispatchEventAsync's generic
            // event construction reliably deliver a real PointerEvent (with pointerId/clientY/button
            // set the way setPointerCapture and this gesture's move handler need) to a
            // pointerCapture-based custom listener in headless Chromium -- confirmed by direct
            // experimentation. Constructing real `new PointerEvent(...)` instances and dispatching
            // them from page script also exercises the shared mobileGestures.js listener.
            bool shadeOpened = await page.EvaluateAsync<bool>(
                """
                async () => {
                    const handle = document.querySelector('.mobile-shade-handle');
                    const fire = (type, clientY) => handle.dispatchEvent(new PointerEvent(type, {
                        bubbles: true, cancelable: true, composed: true,
                        pointerId: 1, pointerType: 'mouse', isPrimary: true,
                        button: 0, buttons: 1, clientX: 0, clientY
                    }));
                    fire('pointerdown', 10);
                    fire('pointermove', 30);
                    fire('pointermove', 70);
                    fire('pointerup', 70);
                    await new Promise(resolve => setTimeout(resolve, 100));
                    return !!document.querySelector('.mobile-shade-panel');
                }
                """);
            Assert.True(shadeOpened, "Dispatching a real PointerEvent swipe sequence did not open the shade.");

            await Assertions.Expect(shadePanel).ToBeVisibleAsync(new() { Timeout = 5000 });
            output.WriteLine("[test] Swipe down past the threshold opened the notification shade.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
