using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// The trimmed Release publish of the Ecosystem host (ADR 0106 §5, ADR 0113) in a real Chromium.
/// Trimming only breaks code the trimmer cannot see being used: the app assemblies the OS
/// lazy-loads and instantiates by the names of their manifests, the commands run in Web Workers,
/// and the sites found by route. Each of those paths is walked once here, on the exact binaries
/// <c>build/publish-trimmed.sh</c> produced, with no console error allowed.
/// </summary>
public sealed partial class TrimmedPublishTests(ITestOutputHelper output)
{
    private const string LoginName = "trimmed";
    private const string DisplayName = "Trimmed Publish";
    private const string Password = "Trimmed-Test-1";

    /// <summary>Generous: the first visit downloads and starts the whole .NET WebAssembly runtime.</summary>
    private const float BootTimeoutMs = 120_000;
    private const float StepTimeoutMs = 30_000;

    /// <summary>
    /// Sign-in, every window app of the Start menu, a pipeline and the worker pool in the terminal,
    /// then the Intranet (ASP.Kazo MVC, ADR 0073) and the sample Blazor site (ADR 0084) in the Browser.
    /// </summary>
    [TrimmedPublishFact]
    public async Task Trimmed_publish_runs_every_app_a_worker_command_and_both_sample_sites()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);
            session.AssertNoErrors("sign-in");

            // --- Every window app: its assembly is lazy-loaded and its entry point found by name ---
            foreach (string app in settings.WindowApps())
            {
                await LaunchAndCloseAsync(page, app);
                session.AssertNoErrors($"launching '{app}'");
            }

            // --- A pipeline in Web Workers (ADR 0070): the command assembly is loaded by name there ---
            await OpenAppAsync(page, "Terminal");
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");
            await RunCommandAsync(page, screen, "seq 30 | grep 1 | wc -l");
            await WaitForTerminalTextAsync(rows, text => Regex.IsMatch(text, @"^\s*12\s*$", RegexOptions.Multiline), "the pipeline's count");
            await RunCommandAsync(page, screen, "clear; cat /proc/kazoos/workers");
            await WaitForTerminalTextAsync(
                rows, text => Regex.IsMatch(text, @"^\d+\s+pool\s", RegexOptions.Multiline), "/proc/kazoos/workers with a pool worker");
            Log("a pipeline ran in the worker pool.");
            session.AssertNoErrors("terminal");
            await CloseAllWindowsAsync(page);

            // --- The Intranet: controllers and .cshtml views from the generated tables (ADR 0082, 0086) ---
            await OpenAppAsync(page, "Browser");
            ILocator address = page.Locator("#browser-address-input");
            await address.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            IFrameLocator frame = page.FrameLocator(".page-frame");
            await GoToAsync(address, "http://intranet.hack/", frame.Locator("#welcome"));
            await Assertions.Expect(frame.Locator("#welcome")).ToHaveTextAsync("Welcome, stranger", new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(frame.Locator("#site-health")).ToHaveTextAsync("up", new() { Timeout = StepTimeoutMs });
            await frame.Locator("#nav-people").ClickAsync();
            await Assertions.Expect(frame.Locator("#people-count")).ToHaveTextAsync("8 result(s)", new() { Timeout = StepTimeoutMs });
            Log("the Intranet served a view, a script and its REST API.");

            // Its Blazor board: routable components found by their [Route] attributes.
            await frame.Locator("#nav-board").ClickAsync();
            ILocator board = page.Locator(".site-view");
            await Assertions.Expect(board.Locator("#column-todo .board-card")).ToHaveCountAsync(2, new() { Timeout = StepTimeoutMs });
            Log("the Intranet's Blazor board rendered.");
            session.AssertNoErrors("Intranet");

            // --- The sample Blazor site: its root component is named by its manifest (web.blazor) ---
            ILocator site = page.Locator(".site-view");
            await GoToAsync(address, "http://blazor-sample.hack/", site.Locator("#counter-value"));
            await Assertions.Expect(site.Locator("#counter-value")).ToHaveTextAsync("0", new() { Timeout = StepTimeoutMs });
            await site.Locator("#counter-button").ClickAsync();
            await Assertions.Expect(site.Locator("#counter-value")).ToHaveTextAsync("1");
            await site.Locator("#link-hello").ClickAsync();
            await Assertions.Expect(site.Locator("#greeting")).ToHaveTextAsync(new Regex("^Hello, World!"), new() { Timeout = StepTimeoutMs });
            Log("the sample Blazor site rendered, counted and called its API.");
            session.AssertNoErrors("Blazor site");

            await CloseAllWindowsAsync(page);
            session.AssertNoErrors("closing every window");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "walk");
            throw;
        }
    }

    /// <summary>
    /// ADR 0017 on the trimmed publish: once the service worker has cached the release, the OS boots
    /// with no network at all, and an app whose assembly was never loaded online still starts (its
    /// trimmed assembly comes from the cache).
    /// </summary>
    [TrimmedPublishFact]
    public async Task Trimmed_publish_boots_offline_after_the_first_load()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.PwaUrl is null)
        {
            Log($"{TrimmedPublishSettings.PwaUrlVariable} is not set: no service worker, offline check skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.PwaUrl);
            await WaitForServiceWorkerControllerAsync(page);
            await SignInAsync(page);
            // The service worker caches the whole release on install (service-worker.published.js).
            await page.WaitForFunctionAsync(
                "async () => { const keys = await caches.keys(); return keys.length > 0 && (await (await caches.open(keys[0])).keys()).length > 50; }",
                null, new PageWaitForFunctionOptions { Timeout = BootTimeoutMs, PollingInterval = 500 });
            Log("the service worker cached the release.");

            await session.Context.SetOfflineAsync(true);
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await SignInAsync(page);
            Log("booted and signed in offline.");

            await LaunchAndCloseAsync(page, "Calculator");
            session.AssertNoErrors("offline");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "offline");
            throw;
        }
        finally
        {
            await session.Context.SetOfflineAsync(false);
        }
    }

    /// <summary>
    /// Waits until the service worker controls the page. Its first activation claims the page,
    /// which reloads once (index.html, <c>controllerchange</c>): that reload is awaited too.
    /// </summary>
    private static async Task WaitForServiceWorkerControllerAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            "() => !!navigator.serviceWorker && !!navigator.serviceWorker.controller",
            null, new PageWaitForFunctionOptions { Timeout = BootTimeoutMs });
        TaskCompletionSource reloaded = new();
        void OnLoad(object? sender, IPage loaded) => reloaded.TrySetResult();
        page.Load += OnLoad;
        try
        {
            await Task.WhenAny(reloaded.Task, Task.Delay(3_000));
        }
        finally
        {
            page.Load -= OnLoad;
        }
    }

    /// <summary>Creates the administrator on first run, or signs in, then waits for the desktop.</summary>
    private async Task SignInAsync(IPage page)
    {
        ILocator onboarding = page.GetByRole(AriaRole.Heading, new() { Name = "Create administrator" });
        ILocator signIn = page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" });
        ILocator launcher = page.GetByRole(AriaRole.Button, new() { Name = "App launcher" });
        ILocator fatal = page.Locator(".fatal-surface, .recovery-surface");

        await onboarding.Or(signIn).Or(fatal).First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = BootTimeoutMs });
        if (await fatal.IsVisibleAsync())
        {
            throw new InvalidOperationException($"The host stopped on an error screen: {await fatal.InnerTextAsync()}");
        }

        if (await onboarding.IsVisibleAsync())
        {
            await page.GetByLabel("Login name").FillAsync(LoginName);
            await page.GetByLabel("Display name").FillAsync(DisplayName);
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
            await page.GetByLabel("Confirm password").FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Create local profile" }).ClickAsync();
        }
        else
        {
            ILocator user = page.GetByLabel("Local user");
            if (await user.Locator($"option[value='{LoginName}']").CountAsync() > 0)
            {
                await user.SelectOptionAsync(LoginName);
            }

            await page.GetByLabel("Password").FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
        }

        await launcher.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = BootTimeoutMs });
        Log("desktop ready.");
    }

    /// <summary>
    /// Launches <paramref name="appName"/>, waits for its window to open with no launch error, then
    /// closes every window.
    /// </summary>
    private async Task LaunchAndCloseAsync(IPage page, string appName)
    {
        ILocator windows = page.Locator("[data-window-id]");
        int before = await windows.CountAsync();
        await OpenAppAsync(page, appName);

        ILocator launchError = page.Locator(".lazy-launch-error");
        // An app asking for dangerous capabilities first needs an administrator (ADR 0048).
        ILocator approval = page.Locator("[role=alertdialog]").Filter(new() { HasText = "Administrator authorization required" });
        bool approved = false;
        Stopwatch elapsed = Stopwatch.StartNew();
        while (await windows.CountAsync() <= before)
        {
            if (!approved && await approval.IsVisibleAsync())
            {
                approved = true;
                await approval.GetByLabel("Password").FillAsync(Password);
                await approval.GetByRole(AriaRole.Button, new() { Name = "Allow" }).ClickAsync();
                Log($"'{appName}' approved by the administrator.");
            }

            if (await launchError.IsVisibleAsync())
            {
                throw new InvalidOperationException($"'{appName}' did not start: {await launchError.InnerTextAsync()}");
            }

            if (elapsed.ElapsedMilliseconds > StepTimeoutMs)
            {
                throw new TimeoutException($"'{appName}' opened no window.");
            }

            await Task.Delay(100);
        }

        // Let the window render its first frame (and fail, if its components were trimmed away).
        await Task.Delay(1_000);
        Assert.False(await launchError.IsVisibleAsync(), $"'{appName}' reported a launch error.");
        Log($"'{appName}' opened a window.");
        await CloseAllWindowsAsync(page);
    }

    /// <summary>
    /// Types <paramref name="url"/> in the Browser's address bar until <paramref name="landmark"/>
    /// shows: the Browser opens on its home page, and a home page still loading when the address is
    /// typed can replace it.
    /// </summary>
    private async Task GoToAsync(ILocator address, string url, ILocator landmark)
    {
        for (int attempt = 1; ; attempt++)
        {
            await address.FillAsync(url);
            await address.PressAsync("Enter");
            try
            {
                await landmark.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 3)
            {
                Log($"{url} not shown yet (attempt {attempt}); typing it again.");
            }
        }
    }

    /// <summary>Opens an app from the Start menu by its exact display name.</summary>
    private async Task OpenAppAsync(IPage page, string appName)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" }).FillAsync(appName);
        await page.GetByRole(AriaRole.Button, new() { Name = $"Launch {appName}", Exact = true }).ClickAsync();
    }

    /// <summary>Types a command line into the xterm.js screen, as a person would.</summary>
    private static async Task RunCommandAsync(IPage page, ILocator screen, string commandLine)
    {
        await screen.ClickAsync();
        await page.Keyboard.TypeAsync(commandLine);
        await page.Keyboard.PressAsync("Enter");
    }

    /// <summary>Polls the terminal's rendered rows until <paramref name="done"/> holds.</summary>
    private async Task<string> WaitForTerminalTextAsync(ILocator rows, Func<string, bool> done, string what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string text = string.Empty;
        while (elapsed.ElapsedMilliseconds < StepTimeoutMs)
        {
            text = (await rows.InnerTextAsync()).Replace(' ', ' ');
            if (done(text))
            {
                return text;
            }

            await Task.Delay(250);
        }

        Log($"terminal text when giving up:\n{text}");
        throw new TimeoutException($"The terminal never showed {what}.");
    }

    /// <summary>Clicks every window's close button until none is left.</summary>
    private static async Task CloseAllWindowsAsync(IPage page)
    {
        ILocator closeButtons = page.Locator("button.chrome-button-close");
        Stopwatch elapsed = Stopwatch.StartNew();
        while (await closeButtons.CountAsync() > 0)
        {
            if (elapsed.ElapsedMilliseconds > StepTimeoutMs)
            {
                throw new TimeoutException($"{await closeButtons.CountAsync()} window(s) did not close.");
            }

            int before = await closeButtons.CountAsync();
            await closeButtons.First.ClickAsync();
            Stopwatch wait = Stopwatch.StartNew();
            while (await closeButtons.CountAsync() >= before && wait.ElapsedMilliseconds < 5_000)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <summary>Retries the first navigation while the static server is still binding its port.</summary>
    private static async Task NavigateWhenReadyAsync(IPage page, string url)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                return;
            }
            catch (PlaywrightException exception)
            {
                last = exception;
                await Task.Delay(1_000);
            }
        }

        throw new InvalidOperationException($"{url} never answered.", last);
    }

    private async Task SaveScreenshotAsync(IPage page, TrimmedPublishSettings settings, string name)
    {
        if (settings.ArtifactsDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(settings.ArtifactsDirectory);
            string path = Path.Combine(settings.ArtifactsDirectory, $"trimmed-{name}-failure.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
            Log($"screenshot: {path}");
        }
        catch (PlaywrightException exception)
        {
            Log($"no screenshot: {exception.Message}");
        }
    }

    private void Log(string message)
    {
        // One prefix per line: publish-trimmed.sh keeps only the lines that carry it.
        foreach (string line in message.Split('\n'))
        {
            output.WriteLine($"[trimmed] {line}");
        }
    }

    /// <summary>The end of an hsh prompt: <c>$</c> or <c>#</c> at the end of a rendered line.</summary>
    [GeneratedRegex(@"[$#]\s*$", RegexOptions.Multiline)]
    private static partial Regex PromptPattern();

    /// <summary>One isolated browser context, with its console errors and page errors recorded.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly List<string> _errors = [];
        private IPlaywright _playwright = null!;
        private IBrowser _browser = null!;

        public IBrowserContext Context { get; private set; } = null!;

        public IPage Page { get; private set; } = null!;

        public static async Task<Session> StartAsync(ITestOutputHelper output)
        {
            Session session = new();
            session._playwright = await Playwright.CreateAsync();
            session._browser = await ChromiumLauncher.LaunchAsync(
                session._playwright, new BrowserTypeLaunchOptions { Headless = true });
            session.Context = await session._browser.NewContextAsync(
                new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
            session.Page = await session.Context.NewPageAsync();
            session.Page.SetDefaultTimeout(StepTimeoutMs);
            session.Page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    lock (session._errors)
                    {
                        session._errors.Add($"console: {message.Text}");
                    }
                }

                if (message.Type is "error" or "warning")
                {
                    output.WriteLine($"[trimmed] [console:{message.Type}] {message.Text}");
                }
            };
            session.Page.PageError += (_, error) =>
            {
                lock (session._errors)
                {
                    session._errors.Add($"page error: {error}");
                }

                output.WriteLine($"[trimmed] [pageerror] {error}");
            };
            return session;
        }

        /// <summary>Fails when any console error or uncaught page error happened so far.</summary>
        public void AssertNoErrors(string step)
        {
            lock (_errors)
            {
                if (_errors.Count > 0)
                {
                    Assert.Fail($"Console errors by the end of '{step}':\n  {string.Join("\n  ", _errors)}");
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _browser.DisposeAsync();
            _playwright.Dispose();
        }
    }
}
