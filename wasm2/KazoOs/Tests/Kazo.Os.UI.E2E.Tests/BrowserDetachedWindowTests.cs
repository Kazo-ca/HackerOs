using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// Detached display windows (ADR 0078, chantier <c>web/10-detached-window</c>) in a real Chromium:
/// a Browser page moves to a second window of the host browser, still served and driven by the OS
/// in the first one. The window's own buttons are Blazor content rendered by the main instance,
/// so clicking them proves Blazor events reach the renderer from another document (ADR 0078 §4).
/// </summary>
public sealed class BrowserDetachedWindowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Browser_page_moves_to_a_detached_window_driven_by_the_os_and_comes_back()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: true, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
                new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await E2ESupport.SeedFixturesAsync(page, output);
            await E2ESupport.OpenAppAsync(page, "Browser", output);

            ILocator addressInput = page.Locator("#browser-address-input");
            await addressInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await addressInput.FillAsync("example.hackeros/page.html");
            await addressInput.PressAsync("Enter");
            await Assertions.Expect(page.FrameLocator(".page-frame").Locator("#script-status"))
                .ToHaveTextAsync("script ran", new() { Timeout = 20000 });

            // --- Detach: the page runs in the new window, its bridge still served by the OS ---
            IPage popup = await page.RunAndWaitForPopupAsync(() => page.Locator("#browser-btn-detach").ClickAsync());
            E2ESupport.AttachDiagnostics(popup, output);
            IFrameLocator detachedFrame = popup.FrameLocator(".page-frame");
            await Assertions.Expect(detachedFrame.Locator("#script-status")).ToHaveTextAsync("script ran", new() { Timeout = 20000 });
            await Assertions.Expect(detachedFrame.Locator("#fetch-status")).ToHaveTextAsync("pong", new() { Timeout = 20000 });
            await Assertions.Expect(popup.Locator("#surface-address")).ToContainTextAsync("example.hackeros/page.html");
            await Assertions.Expect(page.Locator(".browser-detached")).ToBeVisibleAsync();
            Assert.Equal(0, await page.Locator(".page-frame").CountAsync());
            Assert.True(await popup.Locator("link[data-hackeros-mirrored]").CountAsync() > 0, "The desktop's style sheets were not mirrored.");
            output.WriteLine("[test] page detached: the frame runs in the second window with its bridge.");

            // --- The OS window still drives the tab: a navigation shows up in the detached window ---
            await addressInput.FillAsync("example.hackeros/form.html");
            await addressInput.PressAsync("Enter");
            await Assertions.Expect(detachedFrame.Locator("#xhr-status")).ToHaveTextAsync("200 xhr pong", new() { Timeout = 20000 });
            await detachedFrame.Locator("#send").ClickAsync();
            await Assertions.Expect(detachedFrame.Locator("#echo")).ToHaveTextAsync("msg=hello bridge", new() { Timeout = 20000 });
            await Assertions.Expect(addressInput).ToHaveValueAsync("https://example.hackeros/echo");
            await Assertions.Expect(popup).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex(".+"));

            // --- A Blazor button inside the detached window: Reattach (ADR 0078 §4 proof) ---
            TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            popup.Close += (_, _) => closed.TrySetResult();
            await popup.Locator("#surface-btn-reattach").ClickAsync();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Assertions.Expect(page.FrameLocator(".page-frame").Locator("#echo"))
                .ToHaveTextAsync("msg=hello bridge", new() { Timeout = 20000 });
            await Assertions.Expect(page.Locator(".browser-detached")).ToHaveCountAsync(0);
            output.WriteLine("[test] Reattach clicked in the detached window brought the page back.");

            // --- Closing the detached window by hand also brings the page back ---
            IPage second = await page.RunAndWaitForPopupAsync(() => page.Locator("#browser-btn-detach").ClickAsync());
            await Assertions.Expect(second.FrameLocator(".page-frame").Locator("#echo"))
                .ToHaveTextAsync("msg=hello bridge", new() { Timeout = 20000 });
            await second.CloseAsync(new PageCloseOptions { RunBeforeUnload = true });
            await Assertions.Expect(page.FrameLocator(".page-frame").Locator("#echo"))
                .ToHaveTextAsync("msg=hello bridge", new() { Timeout = 20000 });
            await Assertions.Expect(page.Locator(".browser-detached")).ToHaveCountAsync(0);
            output.WriteLine("[test] closing the detached window by hand brought the page back.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
