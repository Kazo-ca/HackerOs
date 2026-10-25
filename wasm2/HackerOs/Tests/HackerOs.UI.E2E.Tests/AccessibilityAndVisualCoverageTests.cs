using System.Diagnostics;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// Expands automated accessibility/visual coverage (P2-GATE-003, doc/wasm/dev/accessibility.md
/// "Required expanded automation") beyond HackerOs.E2E.Tests's seven isolated-harness
/// scenarios: scans the real desktop shell/launcher/taskbar and File Explorer, Settings,
/// Code Editor, and Hack Paint windows, at both desktop and mobile viewports, and persists
/// a screenshot per surface/viewport under artifacts/playwright/ (doc/wasm/dev/accessibility.md's
/// prescribed location) instead of discarding it — the one existing screenshot in this repo
/// (IndexedDbBrowserContractTests) was written to a temp file and deleted immediately after
/// a non-zero-length check, leaving nothing for anyone to actually review.
///
/// Keyboard-only navigation, the high-contrast layer and text at 200% are covered by
/// AccessibilityPreferencesTests (ADR 0105, doc/wasm/dev/accessibility.md). Focus traps, long text,
/// RTL and screen-reader passes remain human checklist items — see
/// doc/wasm/dev/old/Human-test-needed-p2-GATE-003.md.
/// </summary>
public sealed class AccessibilityAndVisualCoverageTests(ITestOutputHelper output)
{
    private static readonly ViewportSize DesktopViewport = new() { Width = 1280, Height = 800 };
    private static readonly ViewportSize MobileViewport = new() { Width = 375, Height = 812 };

    [Fact]
    public async Task Representative_real_app_surfaces_have_no_axe_violations_and_no_mobile_overflow()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        string screenshotDirectory = Path.Combine(solutionDirectory, "artifacts", "playwright", "accessibility");
        Directory.CreateDirectory(screenshotDirectory);

        List<string> findings = [];

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = DesktopViewport,
            });
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            // --- Desktop viewport: idle shell, launcher, and each first-party app window ---
            await ScanAndCaptureAsync(page, "desktop-idle-shell", screenshotDirectory, findings, output);

            await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
            try
            {
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" })
                    .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            }
            catch (TimeoutException)
            {
                // A launcher mount failure previously surfaced only as an opaque locator timeout.
                // Preserve the post-click frame and root text so CI identifies whether the trigger,
                // component initialization, or the host error boundary failed.
                string diagnosticPath = Path.Combine(screenshotDirectory, "desktop-app-launcher-timeout.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = diagnosticPath, FullPage = true });
                string expanded = await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
                    .GetAttributeAsync("aria-expanded") ?? "missing";
                output.WriteLine($"[test] launcher timeout: aria-expanded={expanded}; screenshot={diagnosticPath}");
                output.WriteLine("[test] root text after launcher click:\n" + await page.Locator("body").InnerTextAsync());
                throw;
            }
            await ScanAndCaptureAsync(page, "desktop-app-launcher", screenshotDirectory, findings, output);
            await page.Keyboard.PressAsync("Escape");

            foreach (string appName in new[] { "File Explorer", "Settings", "Code Editor", "Hack Paint" })
            {
                await E2ESupport.OpenAppAsync(page, appName, output);
                await page.WaitForTimeoutAsync(500); // let the window finish its mount animation/render
                string surface = "desktop-" + appName.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant();
                await ScanAndCaptureAsync(page, surface, screenshotDirectory, findings, output);
                if (string.Equals(appName, "Settings", StringComparison.Ordinal))
                {
                    await ExerciseDesktopThemeSwitchAsync(page, screenshotDirectory, findings, output);
                }
            }

            // --- Mobile viewport: representative re-check (idle shell + one app window) ---
            await page.SetViewportSizeAsync(MobileViewport.Width, MobileViewport.Height);
            await page.WaitForTimeoutAsync(500);

            bool hasHorizontalOverflow = await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
            if (hasHorizontalOverflow)
            {
                findings.Add("mobile-file-explorer: page has horizontal overflow at 375px width");
            }
            await ScanAndCaptureAsync(page, "mobile-file-explorer", screenshotDirectory, findings, output);

            if (findings.Count > 0)
            {
                output.WriteLine("[test] Findings:\n" + string.Join('\n', findings));
            }
            Assert.True(findings.Count == 0, $"{findings.Count} accessibility/layout finding(s); see test output for detail.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Proves the real Settings picker updates the root scope before restoring the fixture theme.</summary>
    private static async Task ExerciseDesktopThemeSwitchAsync(
        IPage page,
        string screenshotDirectory,
        List<string> findings,
        ITestOutputHelper output)
    {
        await page.GetByRole(AriaRole.Img, new() { Name = "Windows 7 theme preview" }).ClickAsync();
        await page.Locator(".theme-scope[data-theme='windows-7'][data-form-factor='desktop']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15000 });
        await ScanAndCaptureAsync(page, "desktop-settings-windows-7", screenshotDirectory, findings, output);

        await page.GetByRole(AriaRole.Img, new() { Name = "HackerOS theme preview" }).ClickAsync();
        await page.Locator(".theme-scope[data-theme='hackeros'][data-form-factor='desktop']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 15000 });
    }

    /// <summary>Runs an axe scan (serious/critical only) and saves a full-page screenshot for one named surface.</summary>
    private static async Task ScanAndCaptureAsync(
        IPage page, string surfaceName, string screenshotDirectory, List<string> findings, ITestOutputHelper output)
    {
        var result = await page.RunAxe();
        var blocking = result.Violations?
            .Where(item => string.Equals(item.Impact, "serious", StringComparison.OrdinalIgnoreCase)
                || string.Equals(item.Impact, "critical", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        if (blocking.Length > 0)
        {
            findings.Add($"{surfaceName}: serious/critical axe violations: {string.Join(", ", blocking.Select(item => item.Id))}");
            foreach (var violation in blocking)
            {
                foreach (var node in violation.Nodes ?? [])
                {
                    string summary = string.Join("; ", (node.All ?? []).Concat(node.Any ?? []).Concat(node.None ?? [])
                        .Select(check => check.Message));
                    output.WriteLine(
                        $"[test]   {surfaceName} :: {violation.Id} :: {node.Target?.Selector} :: {node.Html} :: {summary}");
                }
            }
        }

        string screenshotPath = Path.Combine(screenshotDirectory, $"{surfaceName}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = true });
        output.WriteLine($"[test] '{surfaceName}': {blocking.Length} blocking axe finding(s); screenshot saved to {screenshotPath}");
    }
}
