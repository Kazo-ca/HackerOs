using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// Shared boot/login/diagnostics plumbing for the Playwright E2E suite. Every helper
/// logs through <see cref="ITestOutputHelper"/> so a failing run's console/server/page
/// state is visible directly in the test output instead of requiring re-runs with
/// screenshots to find where things went wrong.
/// </summary>
internal static class E2ESupport
{
    public const string LoginName = "admin";
    public const string DisplayName = "Administrator";
    public const string Password = "Admin123";

    public static string HomePath => $"/home/{LoginName}";

    /// <summary>Starts the E2E test harness process, optionally with the gated Test Demo control enabled.</summary>
    public static Process StartHarness(string solutionDirectory, string address, bool testDemo, ITestOutputHelper output)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = solutionDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Without this, "dotnet run" defaults ASPNETCORE_ENVIRONMENT to Production when the shell
        // that invoked `dotnet test` hasn't set it (e.g. plain CI shells). In Production,
        // StaticAssetDevelopmentRuntimeHandler still runs but expects every static web asset to
        // exist as a physical file under this project's own wwwroot, which fails for assets
        // composed from referenced projects/packages (MudBlazor, HackerOs.Ecosystem) and 500s
        // every request.
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("test/test/test.csproj");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(address);
        if (testDemo)
        {
            // dotnet run requires an explicit "--" separator before args meant for the
            // application itself; without it, dotnet run tries to parse --test-demo as
            // one of its own options and fails. See doc/en/wasm/dev/old/e2e-test-demo-mode.md.
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("--test-demo");
        }

        output.WriteLine($"[harness] starting: dotnet {string.Join(' ', startInfo.ArgumentList)}");

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the browser harness.");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.WriteLine($"[server-out] {e.Data}");
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) output.WriteLine($"[server-err] {e.Data}");
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    public static void StopProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
    }

    public static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HackerOs.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate HackerOs.sln.");
    }

    public static int ReservePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Subscribes to page console/error/network-failure events and logs them live.</summary>
    /// <returns>The list of captured console error messages, growing as the test runs.</returns>
    public static List<string> AttachDiagnostics(IPage page, ITestOutputHelper output)
    {
        List<string> consoleErrors = [];
        page.Console += (_, message) =>
        {
            output.WriteLine($"[console:{message.Type}] {message.Text}");
            if (message.Type == "error")
            {
                consoleErrors.Add(message.Text);
            }
        };
        page.PageError += (_, error) => output.WriteLine($"[pageerror] {error}");
        page.RequestFailed += (_, request) =>
            output.WriteLine($"[requestfailed] {request.Method} {request.Url} — {request.Failure}");
        return consoleErrors;
    }

    public static async Task NavigateWhenReadyAsync(IPage page, string address, ITestOutputHelper output)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await page.GotoAsync(address, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                output.WriteLine($"[harness] navigated to {address} after {attempt + 1} attempt(s).");
                return;
            }
            catch (PlaywrightException exception)
            {
                lastFailure = exception;
                await Task.Delay(500);
            }
        }

        output.WriteLine($"[harness] navigation never succeeded: {lastFailure}");
        throw new InvalidOperationException("The browser harness did not become ready.", lastFailure);
    }

    /// <summary>Runs the local-profile onboarding form and waits for the desktop to be ready.</summary>
    public static async Task CreateLocalProfileAndReachDesktopAsync(IPage page, ITestOutputHelper output)
    {
        ILocator setupLogin = page.GetByLabel("Login name");
        await setupLogin.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        await setupLogin.FillAsync(LoginName);
        await page.GetByLabel("Display name").FillAsync(DisplayName);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByLabel("Confirm password").FillAsync(Password);

        await page.GetByRole(AriaRole.Button, new() { Name = "Create local profile" }).ClickAsync();
        output.WriteLine("[harness] submitted local-profile onboarding form.");

        ILocator appLauncherBtn = page.GetByRole(AriaRole.Button, new() { Name = "App launcher" });
        await appLauncherBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        output.WriteLine("[harness] desktop is ready (App launcher visible).");
    }

    /// <summary>Opens an app from the App Launcher by its display name and waits briefly for its window to mount.</summary>
    public static async Task OpenAppAsync(IPage page, string appDisplayName, ITestOutputHelper output)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" }).FillAsync(appDisplayName);

        // Each result exposes an exact primary command distinct from its adjacent Pin/Unpin
        // control. Matching that stable accessible name avoids collisions between similarly
        // named applications and remains independent of the launcher's visual layout.
        ILocator launchButton = page.GetByRole(
            AriaRole.Button,
            new() { Name = $"Launch {appDisplayName}", Exact = true });
        await launchButton.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await launchButton.ClickAsync();
        output.WriteLine($"[harness] launched app '{appDisplayName}'.");
    }

    /// <summary>
    /// Selects one File Explorer entry by clicking its row directly (FileViewDetails.razor:
    /// <c>&lt;tr class="entry-row"&gt;</c> has its own <c>@onclick</c> that calls
    /// <c>FileViewItem.Select</c> — there is no separate checkbox control since
    /// FileExplorerWindow was migrated onto the shared FileView component).
    /// </summary>
    /// <remarks>
    /// A row can be clicked while the listing is still being reloaded (a navigation typed in the
    /// address bar just before): the reload then rebuilds the rows and drops the selection, as a
    /// real file manager does. The row is clicked again until it is still selected after the
    /// listing has settled, instead of assuming the reload finished first.
    /// </remarks>
    public static async Task SelectFileExplorerEntryAsync(IPage page, string entryName, ITestOutputHelper output)
    {
        ILocator row = page.Locator("tr.entry-row", new PageLocatorOptions { HasText = entryName });
        for (int attempt = 1; ; attempt++)
        {
            await row.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await row.ClickAsync();
            await page.WaitForTimeoutAsync(500);
            string? classes = await row.GetAttributeAsync("class");
            if (classes?.Split(' ').Contains("selected") == true || attempt >= 5)
            {
                break;
            }

            output.WriteLine($"[test] '{entryName}' lost its selection to a listing reload; clicking again.");
        }

        output.WriteLine($"[test] selected '{entryName}' via its row.");
    }

    /// <summary>
    /// Clicks the E2E-only "Test Demo" control and waits for the seed operation to
    /// report completion. Fails loudly (with the reported error text) if seeding
    /// itself failed, instead of letting a later assertion fail on missing files with
    /// no indication why they are missing.
    /// </summary>
    public static async Task<string> SeedFixturesAsync(IPage page, ITestOutputHelper output)
    {
        ILocator button = page.Locator("#btn-test-demo");
        await button.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await button.ClickAsync();

        ILocator status = page.Locator("#test-demo-status");
        await status.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
        string text = await status.InnerTextAsync();
        output.WriteLine($"[harness] test-demo-status: {text}");

        if (!text.StartsWith("Seeded:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Test Demo fixture seeding failed: {text}");
        }

        return text["Seeded:".Length..];
    }

    private const string CaptureInitScript = """
        window.__e2eCaptures = [];
        const __e2eUrlToBlob = new Map();
        const __e2eOriginalCreateObjectURL = URL.createObjectURL.bind(URL);
        URL.createObjectURL = (blob) => {
            const url = __e2eOriginalCreateObjectURL(blob);
            __e2eUrlToBlob.set(url, blob);
            return url;
        };
        const __e2eOriginalAnchorClick = HTMLAnchorElement.prototype.click;
        HTMLAnchorElement.prototype.click = function () {
            if (this.href && __e2eUrlToBlob.has(this.href)) {
                window.__e2eCaptures.push({ fileName: this.download, blob: __e2eUrlToBlob.get(this.href) });
            }
            return __e2eOriginalAnchorClick.call(this);
        };
        """;

    /// <summary>
    /// Installs a page-init script that intercepts the same client-side download
    /// mechanism File Explorer uses (Apps/System/HackerOs.Apps.FileExplorer's
    /// download feature builds a Blob and triggers a save via a synthetic
    /// <c>&lt;a download&gt;</c> click — see Shared/HackerOs.AppSdk.Blazor/wwwroot/download.js)
    /// and records the file name and Blob at the exact moment of the click. Chromium/CDP
    /// does not reliably surface that pattern as a Playwright "Download" event once the
    /// click happens several async hops after the real user gesture (confirmed working
    /// correctly at the OS/browser level independently of Playwright — this is a
    /// download-*event* capture gap, not an app bug). Reading the Blob directly here is
    /// deterministic and avoids depending on the OS download flow entirely. Must be
    /// called before navigation.
    /// </summary>
    public static Task InstallBlobCaptureAsync(IPage page) => page.AddInitScriptAsync(CaptureInitScript);

    /// <summary>
    /// Runs <paramref name="trigger"/>, waits for one more captured download (see
    /// <see cref="InstallBlobCaptureAsync"/>), and returns its file name and raw bytes.
    /// </summary>
    public static async Task<(string FileName, byte[] Bytes)> CaptureNextDownloadAsync(
        IPage page, Func<Task> trigger, ITestOutputHelper output)
    {
        int countBefore = await page.EvaluateAsync<int>("window.__e2eCaptures.length");
        await trigger();

        await page.WaitForFunctionAsync(
            "count => window.__e2eCaptures.length > count",
            countBefore,
            new PageWaitForFunctionOptions { Timeout = 15000 });

        string fileName = await page.EvaluateAsync<string>(
            "window.__e2eCaptures[window.__e2eCaptures.length - 1].fileName");
        string base64 = await page.EvaluateAsync<string>("""
            async () => {
                const blob = window.__e2eCaptures[window.__e2eCaptures.length - 1].blob;
                const bytes = new Uint8Array(await blob.arrayBuffer());
                let binary = '';
                for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i]);
                return btoa(binary);
            }
            """);
        byte[] bytes = Convert.FromBase64String(base64);
        output.WriteLine($"[test] captured download '{fileName}': {bytes.Length} bytes.");
        return (fileName, bytes);
    }
}
