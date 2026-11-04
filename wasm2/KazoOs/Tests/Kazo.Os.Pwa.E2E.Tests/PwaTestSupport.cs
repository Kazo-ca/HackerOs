using Microsoft.Playwright;

namespace Kazo.Os.Pwa.E2E.Tests;

/// <summary>Shared boot/onboarding/service-worker plumbing for the published-PWA suite.</summary>
internal static class PwaTestSupport
{
    public const string LoginName = "admin";
    public const string DisplayName = "Administrator";
    public const string Password = "Admin123";

    public static async Task NavigateWhenReadyAsync(IPage page, string address)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await page.GotoAsync(address, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                return;
            }
            catch (PlaywrightException exception)
            {
                lastFailure = exception;
                await Task.Delay(200);
            }
        }

        throw new InvalidOperationException("The published app did not become reachable.", lastFailure);
    }

    /// <summary>Subscribes to console/page/network-failure diagnostics, matching the pattern used across this repo's E2E suites.</summary>
    public static List<string> AttachDiagnostics(IPage page)
    {
        List<string> failures = [];
        page.Console += (_, message) =>
        {
            Console.WriteLine($"[console:{message.Type}] {message.Text}");
            if (message.Type == "error") failures.Add($"console: {message.Text}");
        };
        page.PageError += (_, error) =>
        {
            Console.WriteLine($"[pageerror] {error}");
            failures.Add($"page: {error}");
        };
        page.RequestFailed += (_, request) =>
        {
            Console.WriteLine($"[requestfailed] {request.Method} {request.Url} — {request.Failure}");
            failures.Add($"network: {request.Method} {request.Url}");
        };
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                Console.WriteLine($"[http{response.Status}] {response.Url}");
                failures.Add($"http{response.Status}: {response.Url}");
            }
        };
        return failures;
    }

    /// <summary>Runs the real onboarding form (same markup/labels as the rest of this repo's E2E suites) and waits for the desktop.</summary>
    public static async Task CreateLocalProfileAndReachDesktopAsync(IPage page)
    {
        ILocator loginField = page.GetByLabel("Login name");
        await loginField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await loginField.FillAsync(LoginName);
        await page.GetByLabel("Display name").FillAsync(DisplayName);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByLabel("Confirm password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create local profile" }).ClickAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
    }

    /// <summary>
    /// Every reload — online or offline — lands on the "Sign in" view for the existing local
    /// profile (App.razor's <c>EcosystemHostView.Login</c>), not straight back on the desktop:
    /// the account persists across reload but the session does not. Re-enters the password and
    /// waits for the desktop.
    /// </summary>
    public static async Task SignInAsync(IPage page)
    {
        ILocator passwordField = page.GetByLabel("Password", new() { Exact = true });
        await passwordField.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await passwordField.FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
    }

    /// <summary>
    /// Waits until the page's active service-worker client controller is set (install +
    /// activate completed), then waits out the one-time self-reload index.html's own
    /// <c>controllerchange</c> listener triggers: per spec, <c>controllerchange</c> also
    /// fires for the very first null-&gt;controller transition (<c>clients.claim()</c> in
    /// <c>onActivate</c>), not only for real updates, so first-install always reloads once.
    /// Callers must await this before evaluating page state, or they race a destroyed
    /// execution context.
    /// </summary>
    public static async Task WaitForServiceWorkerControllerAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => !!navigator.serviceWorker.controller",
            new PageWaitForFunctionOptions { Timeout = 30000 });

        TaskCompletionSource reloaded = new();
        void OnLoad(object? _, IPage _1) => reloaded.TrySetResult();
        page.Load += OnLoad;
        try
        {
            await Task.WhenAny(reloaded.Task, Task.Delay(3000));
        }
        finally
        {
            page.Load -= OnLoad;
        }
    }

    /// <summary>Body written over a cached boot resource by <see cref="CorruptCachedBootResourceAsync"/>.</summary>
    public const string CorruptedBody = "CORRUPTED-BY-TEST";

    /// <summary>Waits for the onboarding form, the first view of a booted OS with no profile yet.</summary>
    public static Task WaitForOnboardingAsync(IPage page, float timeoutMs = 20000) =>
        page.GetByLabel("Login name").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = timeoutMs });

    /// <summary>
    /// Overwrites <c>System.Private.CoreLib.*.wasm</c> (needed by every boot) in the service
    /// worker's release cache, as a damaged disk or an interrupted write would, and returns its URL.
    /// </summary>
    public static Task<string> CorruptCachedBootResourceAsync(IPage page, string cacheName) =>
        page.EvaluateAsync<string>(
            """
            async ([cacheName, body]) => {
                const cache = await caches.open(cacheName);
                const target = (await cache.keys()).find(r => /System\.Private\.CoreLib\.[^/]*\.wasm$/.test(r.url));
                await cache.put(target, new Response(body, { headers: { 'Content-Type': 'application/wasm' } }));
                return target.url;
            }
            """,
            new object[] { cacheName, CorruptedBody });

    /// <summary>Byte length of <paramref name="url"/> in the given cache, or -1 when it is absent.</summary>
    public static Task<int> GetCachedLengthAsync(IPage page, string cacheName, string url) =>
        page.EvaluateAsync<int>(
            """
            async ([cacheName, url]) => {
                const response = await (await caches.open(cacheName)).match(url);
                return response ? (await response.arrayBuffer()).byteLength : -1;
            }
            """,
            new object[] { cacheName, url });

    public static Task<string[]> GetCacheKeysAsync(IPage page) =>
        page.EvaluateAsync<string[]>("async () => await caches.keys()");

    /// <summary>
    /// The service worker's caches other than the release ones: <c>hackeros-isolation</c> keeps the
    /// cross-origin isolation choice of this origin across releases (ADR 0131 §5, ADR 0143), so it is
    /// never purged with an old release.
    /// </summary>
    public static readonly IReadOnlySet<string> ReleaseIndependentCaches = new HashSet<string>(StringComparer.Ordinal) { "hackeros-isolation" };

    /// <summary>
    /// Asserts that the only release cache is <paramref name="expectedReleaseCache"/> and that every
    /// other cache is one of <see cref="ReleaseIndependentCaches"/>.
    /// </summary>
    public static async Task AssertOnlyReleaseCacheAsync(IPage page, string expectedReleaseCache)
    {
        string[] cacheKeys = await GetCacheKeysAsync(page);
        Assert.Equal([expectedReleaseCache], cacheKeys.Where(key => key.StartsWith("hackeros-cache-", StringComparison.Ordinal)));
        Assert.All(cacheKeys.Where(key => !key.StartsWith("hackeros-cache-", StringComparison.Ordinal)),
            key => Assert.Contains(key, ReleaseIndependentCaches));
    }

    public static Task<string> GetAssetsManifestVersionAsync(IPage page) =>
        page.EvaluateAsync<string>("async () => (await (await fetch('service-worker-assets.js')).text()).match(/\"version\"\\s*:\\s*\"([^\"]+)\"/)[1]");

    /// <summary>
    /// Opens an app from the App Launcher by its display name (same pattern as
    /// Kazo.Os.UI.E2E.Tests.E2ESupport.OpenAppAsync). AppLauncher.razor renders each search
    /// result as a grid row with a "Launch {name}" button — not an ARIA option — so the button's
    /// accessible name is what must be matched here.
    /// </summary>
    public static async Task OpenAppAsync(IPage page, string appDisplayName)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" }).FillAsync(appDisplayName);
        ILocator launchButton = page.GetByRole(
            AriaRole.Button,
            new() { Name = $"Launch {appDisplayName}", Exact = true });
        await launchButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await launchButton.ClickAsync();
    }

    /// <summary>
    /// Creates <paramref name="fileName"/> in <paramref name="directory"/> through File
    /// Explorer's "New File" action (<c>FileExplorerWindow.CreateFileAsync</c>, which calls
    /// <c>FileSystem.CreateAsync</c>), then writes <paramref name="content"/> to it through
    /// Text Editor's "Save As" onto that same path (confirming the overwrite prompt), which
    /// resolves to <c>FileSystem.WriteAsync</c> against a now-*existing* target. Used to prove
    /// file persistence survives reload/offline/update.
    ///
    /// Originally routed around two confirmed Text Editor gaps. The first — "Save As" straight
    /// onto a path that has never existed failing with "Destination directory not found",
    /// because <c>WriteToDiskAsync</c> only ever called <c>FileSystem.WriteAsync</c> (an atomic
    /// REPLACEMENT requiring a pre-existing target) even for a document with no prior
    /// revision/path — is now fixed: <c>WriteToDiskAsync</c> calls <c>FileSystem.CreateAsync</c>
    /// first when the document has never been persisted. The File-Explorer-then-Save-As dance
    /// here remains necessary for the second, still-open gap: opening the freshly created
    /// *empty* file in Text Editor reports "Binary files are not supported by Text Editor" — a
    /// zero-length file fails its text/binary detection. Save As (which never reads the
    /// target's current bytes) avoids that path entirely.
    /// </summary>
    public static async Task CreateAndSaveFileAsync(IPage page, string directory, string fileName, string content)
    {
        await OpenAppAsync(page, "File Explorer");
        ILocator addressInput = page.GetByLabel("Current path address");
        await addressInput.FillAsync(directory);
        await addressInput.PressAsync("Enter");

        await page.GetByRole(AriaRole.Button, new() { Name = "New File" }).ClickAsync();
        ILocator inputDialog = page.Locator(".text-input-dialog");
        await inputDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await inputDialog.Locator(".dialog-input").FillAsync(fileName);
        await inputDialog.Locator("button.primary").ClickAsync();
        await inputDialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 20000 });

        ILocator row = page.Locator("tr.entry-row", new PageLocatorOptions { HasText = fileName });
        await row.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });

        await OpenAppAsync(page, "Text Editor");
        ILocator textarea = page.Locator("#editor-textarea");
        await textarea.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await textarea.FillAsync(content);

        ILocator menu = page.GetByLabel("Text editor menu");
        await menu.GetByText("File", new() { Exact = true }).HoverAsync();
        await page.Locator("#btn-save-as").ClickAsync();

        // Not GetByRole(Dialog, Name: "Save Text File"): the window chrome hosting this dialog is
        // itself role="dialog" and (correctly, since the WindowHost TitleId binding fix) now exposes
        // the same "Save Text File" accessible name, so that locator resolves ambiguously to both the
        // window and the actual file-picker section nested inside it. section.file-dialog is that
        // inner element specifically (SaveFileDialog.razor).
        ILocator dialog = page.Locator("section.file-dialog");
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await dialog.Locator(".name-control input").FillAsync(fileName);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        ILocator overwrite = dialog.GetByRole(AriaRole.Alertdialog, new() { Name = "Confirm overwrite" });
        await overwrite.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await overwrite.GetByRole(AriaRole.Button, new() { Name = "Replace" }).ClickAsync();
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 20000 });

        // The dialog closes as soon as a path is picked, but TextEditorWindow's SaveAsAsync
        // still has an awaited WriteToDiskAsync VFS round trip left to run afterward. #btn-save
        // disables once _doc.IsDirty clears, which only happens once that write completes.
        await menu.GetByText("File", new() { Exact = true }).HoverAsync();
        ILocator saveButton = page.Locator("#btn-save");
        await Assertions.Expect(saveButton).ToBeDisabledAsync(new LocatorAssertionsToBeDisabledOptions { Timeout = 20000 });
    }

    private const string CaptureInitScript = """
        window.__pwaCaptures = [];
        const __pwaUrlToBlob = new Map();
        const __pwaOriginalCreateObjectURL = URL.createObjectURL.bind(URL);
        URL.createObjectURL = (blob) => {
            const url = __pwaOriginalCreateObjectURL(blob);
            __pwaUrlToBlob.set(url, blob);
            return url;
        };
        const __pwaOriginalAnchorClick = HTMLAnchorElement.prototype.click;
        HTMLAnchorElement.prototype.click = function () {
            if (this.href && __pwaUrlToBlob.has(this.href)) {
                window.__pwaCaptures.push({ fileName: this.download, blob: __pwaUrlToBlob.get(this.href) });
            }
            return __pwaOriginalAnchorClick.call(this);
        };
        """;

    /// <summary>
    /// Installs the same client-side download intercept as Kazo.Os.UI.E2E.Tests.E2ESupport
    /// (Chromium/CDP does not reliably surface File Explorer's Blob-download pattern as a
    /// Playwright "Download" event). Must be called via <see cref="IPage.AddInitScriptAsync"/>
    /// before the first navigation so it is present when the page's own scripts install their
    /// handlers.
    /// </summary>
    public static Task InstallBlobCaptureAsync(IPage page) => page.AddInitScriptAsync(CaptureInitScript);

    /// <summary>Navigates File Explorer to the given directory and asserts the entry downloads with the expected content.</summary>
    public static async Task AssertFileContentAsync(IPage page, string directory, string fileName, string expectedContent)
    {
        await OpenAppAsync(page, "File Explorer");
        ILocator addressInput = page.GetByLabel("Current path address");
        await addressInput.FillAsync(directory);
        await addressInput.PressAsync("Enter");

        ILocator row = page.Locator("tr.entry-row", new PageLocatorOptions { HasText = fileName });
        // The listing can reload once more right after the navigation and drop the selection
        // (same race Kazo.Os.UI.E2E.Tests' SelectFileExplorerEntryAsync handles): click until the
        // row reports itself selected, so "Download" is enabled.
        for (int attempt = 1; ; attempt++)
        {
            await row.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20000 });
            await row.ClickAsync();
            await page.WaitForTimeoutAsync(500);
            string? classes = await row.GetAttributeAsync("class");
            if (classes?.Split(' ').Contains("selected") == true || attempt >= 5)
            {
                break;
            }
        }

        int countBefore = await page.EvaluateAsync<int>("window.__pwaCaptures.length");
        await page.GetByRole(AriaRole.Button, new() { Name = "Download" }).ClickAsync();
        await page.WaitForFunctionAsync(
            "count => window.__pwaCaptures.length > count", countBefore,
            new PageWaitForFunctionOptions { Timeout = 20000 });

        string base64 = await page.EvaluateAsync<string>("""
            async () => {
                const blob = window.__pwaCaptures[window.__pwaCaptures.length - 1].blob;
                const bytes = new Uint8Array(await blob.arrayBuffer());
                let binary = '';
                for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                return btoa(binary);
            }
            """);
        string actualContent = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        if (actualContent != expectedContent)
        {
            throw new InvalidOperationException(
                $"Expected '{fileName}' content '{expectedContent}' but downloaded '{actualContent}'.");
        }
    }
}
