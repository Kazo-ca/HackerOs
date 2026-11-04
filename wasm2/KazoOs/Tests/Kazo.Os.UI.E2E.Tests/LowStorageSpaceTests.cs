using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// Low storage space against Chromium's real quota (ADR 0099 §2 and §3): the origin's quota is
/// lowered with <c>Storage.overrideQuotaForOrigin</c> until the free space falls inside the root
/// reserve, like a disk filling up under a running system.
/// </summary>
public sealed class LowStorageSpaceTests(ITestOutputHelper output)
{
    /// <summary>
    /// The session user gets one "Low storage space" notification whose action opens Diagnostic,
    /// and the system keeps writing inside the root reserve: its logs flush and the administrator
    /// still signs in after a reload.
    /// </summary>
    [Fact]
    public async Task A_full_quota_warns_once_and_the_system_keeps_writing_in_the_root_reserve()
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
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            // Leave 3 % of the quota free: low (under 10 %) and inside the 5 % root reserve.
            ICDPSession cdp = await context.NewCDPSessionAsync(page);
            double usage = await page.EvaluateAsync<double>("async () => (await navigator.storage.estimate()).usage");
            double quota = Math.Ceiling(usage / 0.97);
            await cdp.SendAsync("Storage.overrideQuotaForOrigin", new Dictionary<string, object>
            {
                ["origin"] = address,
                ["quotaSize"] = quota
            });
            JsonElement estimate = await page.EvaluateAsync<JsonElement>("async () => await navigator.storage.estimate()");
            output.WriteLine($"[test] usage {usage} B, quota lowered to {estimate.GetProperty("quota").GetDouble()} B.");

            // The system's own log flushes ask for a new measure; the periodic one runs every 30 s.
            ILocator clockButton = page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" });
            ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            ILocator warning = panel.GetByRole(AriaRole.Heading, new() { Name = "Low storage space" });
            Stopwatch waited = Stopwatch.StartNew();
            while (true)
            {
                await clockButton.ClickAsync();
                await Assertions.Expect(panel).ToBeVisibleAsync();
                if (await warning.CountAsync() > 0)
                {
                    break;
                }

                Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), "No low-space notification within 60 s.");
                await page.Keyboard.PressAsync("Escape");
                await page.WaitForTimeoutAsync(3000);
            }

            output.WriteLine($"[test] low-space notification after {waited.Elapsed.TotalSeconds:0.#} s.");
            await Assertions.Expect(warning).ToHaveCountAsync(1);
            await Assertions.Expect(panel.GetByRole(AriaRole.Button, new() { Name = "Open Diagnostic" })).ToBeVisibleAsync();

            // Inside the reserve the system still writes: the session ends cleanly and the
            // administrator's sign-in (auth.log, session files) works after a reload.
            await page.Keyboard.PressAsync("Escape");
            await page.ReloadAsync();
            ILocator passwordField = page.GetByLabel("Password", new() { Exact = true });
            await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
            await passwordField.FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
            await clockButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30000 });
            output.WriteLine("[test] signed in again inside the root reserve.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
