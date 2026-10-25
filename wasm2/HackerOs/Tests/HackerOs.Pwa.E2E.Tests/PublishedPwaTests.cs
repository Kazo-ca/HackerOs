using System.Text.Json;
using Microsoft.Playwright;

namespace HackerOs.Pwa.E2E.Tests;

/// <summary>
/// Exercises the real published Release output of OS/HackerOs.Ecosystem — the exact static
/// site <c>deploy-wasm.yml</c> publishes to GitHub Pages — against the actual
/// <c>service-worker.published.js</c> and <c>manifest.webmanifest</c>. This is the matrix
/// described by <c>doc/en/wasm/dev/old/integration-audit-remediation.md</c> Wave C / <c>P2-PWA-007</c>:
/// first online visit, installability, service-worker activation, server-unavailable
/// reload, real browser-offline reload, file/settings persistence, update-waiting/safe
/// activation, and release-asset consistency (P2-ACC-015 / P2-ACC-016), plus the
/// corrupted-cache recovery of ADR 0099 §3, online and offline.
///
/// Browser-support policy: Chromium only (same as every other Playwright suite in this
/// repo, launched with <c>Channel = "chrome"</c>). Firefox/Safari are not covered by
/// automated or manual evidence.
/// </summary>
[Collection(PwaPublishCollection.Name)]
public sealed class PublishedPwaTests(PwaPublishFixture fixture)
{
    [Fact]
    public async Task First_online_visit_registers_and_activates_the_service_worker_with_a_populated_cache()
    {
        int port = PublishedAppHost.ReservePort();
        await using PublishedAppHost host = new();
        await host.StartAsync(fixture.V1WwwrootPath, port);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        IPage page = await browser.NewPageAsync();
        List<string> failures = PwaTestSupport.AttachDiagnostics(page);

        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);

        // Installability: a real <link rel="manifest"> pointing at a valid, standalone-display manifest.
        string? manifestHref = await page.EvaluateAsync<string?>(
            "() => document.querySelector('link[rel=manifest]')?.getAttribute('href')");
        Assert.Equal("manifest.webmanifest", manifestHref);
        IAPIResponse manifestResponse = await page.APIRequest.GetAsync($"{host.Address}/manifest.webmanifest");
        Assert.True(manifestResponse.Ok);
        using JsonDocument manifest = JsonDocument.Parse(await manifestResponse.TextAsync());
        Assert.Equal("standalone", manifest.RootElement.GetProperty("display").GetString());

        // WaitForServiceWorkerControllerAsync absorbs index.html's self-reload (controllerchange
        // fires even for the first null->controller transition). That reload legitimately aborts
        // whatever the abandoned pre-reload document had in flight (e.g. a lazily-imported JS
        // interop module) with net::ERR_ABORTED — collateral of a real, already-documented
        // behavior, not a defect in the reloaded page this test actually evaluates below.
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
        failures.Clear();

        string version = await PwaTestSupport.GetAssetsManifestVersionAsync(page);
        await PwaTestSupport.AssertOnlyReleaseCacheAsync(page, $"hackeros-cache-{version}");

        Assert.Empty(failures);
    }

    [Fact]
    public async Task Reload_after_the_server_stops_still_serves_the_app_and_committed_profile_from_cache()
    {
        int port = PublishedAppHost.ReservePort();
        PublishedAppHost host = new();
        try
        {
            await host.StartAsync(fixture.V1WwwrootPath, port);

            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            PwaTestSupport.AttachDiagnostics(page);

            await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
            await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
            await PwaTestSupport.CreateLocalProfileAndReachDesktopAsync(page);

            // P2-ACC-015 (server unavailable): stop the origin entirely, then reload.
            await host.StopAsync();
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            // The cached shell booted and the committed local profile was read back from
            // IndexedDB even though the origin server is down: it lands on Sign In (not the
            // onboarding form, which only appears with zero existing profiles), and signing
            // back in reaches the desktop with no network involved.
            await PwaTestSupport.SignInAsync(page);
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reload_fully_offline_boots_the_app_and_preserves_a_saved_file()
    {
        int port = PublishedAppHost.ReservePort();
        await using PublishedAppHost host = new();
        await host.StartAsync(fixture.V1WwwrootPath, port);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        await PwaTestSupport.InstallBlobCaptureAsync(page);
        PwaTestSupport.AttachDiagnostics(page);

        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
        await PwaTestSupport.CreateLocalProfileAndReachDesktopAsync(page);

        const string content = "Written while online, read back offline.\n";
        string directory = $"/home/{PwaTestSupport.LoginName}";
        await PwaTestSupport.CreateAndSaveFileAsync(page, directory, "offline-proof.txt", content);

        // Real offline (P2-ACC-015): not just "server stopped" — the browser's own network
        // stack is cut, exactly as doc/en/wasm/dev/adr/0017-pwa-cache-and-offline-strategy.md requires.
        await context.SetOfflineAsync(true);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        // The profile already exists (committed before going offline), so this is Sign In,
        // not onboarding — and it, and the desktop after it, work with zero network access.
        await PwaTestSupport.SignInAsync(page);

        await PwaTestSupport.AssertFileContentAsync(page, directory, "offline-proof.txt", content);
    }

    [Fact]
    public async Task Update_to_a_new_release_purges_the_old_cache_and_preserves_compatible_data()
    {
        int port = PublishedAppHost.ReservePort();
        PublishedAppHost host = new();
        await host.StartAsync(fixture.V1WwwrootPath, port);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        IPage page = await browser.NewPageAsync();
        await PwaTestSupport.InstallBlobCaptureAsync(page);
        PwaTestSupport.AttachDiagnostics(page);

        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
        await PwaTestSupport.CreateLocalProfileAndReachDesktopAsync(page);

        const string content = "Present before and after the update.\n";
        string directory = $"/home/{PwaTestSupport.LoginName}";
        await PwaTestSupport.CreateAndSaveFileAsync(page, directory, "update-proof.txt", content);

        string v1Version = await PwaTestSupport.GetAssetsManifestVersionAsync(page);

        // Swap the origin to serve v2's published output on the same address, then force the
        // browser to check for a new service-worker script (updateViaCache:'none' on
        // registration means the script itself is always revalidated over the network).
        await host.StopAsync();
        await host.StartAsync(fixture.V2WwwrootPath, port);

        await page.EvaluateAsync("""
            () => {
                window.__pwaUpdateAvailable = false;
                window.addEventListener('hackeros-pwa-update-available', () => { window.__pwaUpdateAvailable = true; });
            }
            """);
        await page.EvaluateAsync(
            "async () => { const reg = await navigator.serviceWorker.getRegistration(); await reg.update(); }");
        await page.WaitForFunctionAsync(
            "() => window.__pwaUpdateAvailable === true",
            new PageWaitForFunctionOptions { Timeout = 30000 });

        TaskCompletionSource reloaded = new();
        void OnLoad(object? _, IPage _1) => reloaded.TrySetResult();
        page.Load += OnLoad;
        try
        {
            // Safe activation: post the same SKIP_WAITING message a future "update" UI would
            // send once the user accepts — index.html's own controllerchange listener then
            // reloads the page.
            await page.EvaluateAsync("""
                async () => {
                    const reg = await navigator.serviceWorker.getRegistration();
                    reg.waiting.postMessage({ type: 'SKIP_WAITING' });
                }
                """);
            await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            page.Load -= OnLoad;
        }

        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        // Already-committed profile survives the update (no onboarding form) — Sign In, then desktop.
        await PwaTestSupport.SignInAsync(page);

        string v2Version = await PwaTestSupport.GetAssetsManifestVersionAsync(page);
        Assert.NotEqual(v1Version, v2Version);
        await PwaTestSupport.AssertOnlyReleaseCacheAsync(page, $"hackeros-cache-{v2Version}");

        await PwaTestSupport.AssertFileContentAsync(page, directory, "update-proof.txt", content);

        await host.DisposeAsync();
    }

    /// <summary>
    /// ADR 0099 §3, online: a boot resource damaged inside the service worker's cache fails
    /// Blazor's integrity check; index.html's boot guard asks the worker (RESET_CACHE) to
    /// refill its cache from the server and reloads once; the app then boots and the cache
    /// holds the genuine bytes again. Replaces the former test that only proved corrupted
    /// bytes were served as they were.
    /// </summary>
    [Fact]
    public async Task Corrupted_boot_resource_in_the_cache_is_downloaded_again_and_the_app_boots_after_one_reload()
    {
        int port = PublishedAppHost.ReservePort();
        await using PublishedAppHost host = new();
        await host.StartAsync(fixture.V1WwwrootPath, port);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        PwaTestSupport.AttachDiagnostics(page);

        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
        await PwaTestSupport.WaitForOnboardingAsync(page);
        string cacheName = $"hackeros-cache-{await PwaTestSupport.GetAssetsManifestVersionAsync(page)}";
        string damagedUrl = await PwaTestSupport.CorruptCachedBootResourceAsync(page, cacheName);

        int loads = 0;
        void OnLoad(object? _, IPage _1) => Interlocked.Increment(ref loads);
        page.Load += OnLoad;
        try
        {
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await PwaTestSupport.WaitForOnboardingAsync(page, timeoutMs: 60000);
        }
        finally
        {
            page.Load -= OnLoad;
        }

        // Our reload, then exactly one recovery reload.
        Assert.Equal(2, loads);
        Assert.True(await PwaTestSupport.GetCachedLengthAsync(page, cacheName, damagedUrl) > PwaTestSupport.CorruptedBody.Length);
        await PwaTestSupport.AssertOnlyReleaseCacheAsync(page, cacheName);
        // A successful boot clears the one-reload guard again.
        Assert.Null(await page.EvaluateAsync<string?>("() => sessionStorage.getItem('hackeros.bootRecovery')"));
    }

    /// <summary>
    /// ADR 0099 §3, offline: the same damaged cache cannot be repaired without the server, so
    /// the worker keeps it untouched and the boot-error screen tells the user to reconnect
    /// once; after reconnecting, Retry repairs the cache and the app boots.
    /// Playwright's offline emulation does not reach the service worker's own fetches, so the
    /// origin server is stopped as well (the worker then really cannot reach it).
    /// </summary>
    [Fact]
    public async Task Corrupted_cache_offline_asks_to_reconnect_once_and_recovers_when_back_online()
    {
        int port = PublishedAppHost.ReservePort();
        await using PublishedAppHost host = new();
        await host.StartAsync(fixture.V1WwwrootPath, port);

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright,
            new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        PwaTestSupport.AttachDiagnostics(page);

        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
        await PwaTestSupport.WaitForOnboardingAsync(page);
        string cacheName = $"hackeros-cache-{await PwaTestSupport.GetAssetsManifestVersionAsync(page)}";
        string damagedUrl = await PwaTestSupport.CorruptCachedBootResourceAsync(page, cacheName);

        await context.SetOfflineAsync(true);
        await host.StopAsync();
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        ILocator bootError = page.Locator("#hackeros-boot-error[data-state='offline']");
        await bootError.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
        await Assertions.Expect(bootError.Locator(".boot-error-offline")).ToBeVisibleAsync();
        await Assertions.Expect(bootError.Locator(".boot-error-offline")).ToContainTextAsync("Reconnect");
        // Nothing was thrown away while offline: the shell can still explain the failure.
        Assert.Equal(PwaTestSupport.CorruptedBody.Length, await PwaTestSupport.GetCachedLengthAsync(page, cacheName, damagedUrl));

        await host.StartAsync(fixture.V1WwwrootPath, port);
        await context.SetOfflineAsync(false);
        await bootError.GetByRole(AriaRole.Button, new() { Name = "Retry" }).ClickAsync();

        await PwaTestSupport.WaitForOnboardingAsync(page, timeoutMs: 60000);
        Assert.True(await PwaTestSupport.GetCachedLengthAsync(page, cacheName, damagedUrl) > PwaTestSupport.CorruptedBody.Length);
    }
}
