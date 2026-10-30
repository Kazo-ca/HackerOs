using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace HackerOs.TrimmedPublish.E2E.Tests;

/// <summary>
/// ADR 0206: the <c>wwwroot/</c> files of an app installed by <c>hackpkg</c> are served at
/// <c>_content/&lt;assembly&gt;/</c> by the service worker, Web Worker scripts included.
/// </summary>
public sealed partial class TrimmedPublishTests
{
    private const string ProbeRepository = "Kazo-ca/HackerOs-App-InstalledAssets";
    private const string ProbeAppName = "Installed Assets Probe";
    private const string ProbeContent = "_content/HackerOs.Fixtures.InstalledAssets/";

    /// <summary>
    /// The test package (<c>Tests/fixtures/installed-app</c>), answered in place of GitHub, is
    /// installed with <c>sudo hackpkg install</c> on the development address (no service worker from
    /// index.html there), then the OS restarts and the app starts: its module, the Web Worker it starts,
    /// the script that worker loads by a relative <c>importScripts</c>, and its image all load.
    /// </summary>
    [TrimmedPublishFact]
    public async Task An_installed_app_finds_its_wwwroot_files_and_its_worker_scripts()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.PackagesDirectory is null)
        {
            Log($"{TrimmedPublishSettings.PackagesVariable} is not set: no test package, installed app check skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        await ServePackagesLocallyAsync(session.Context, settings.PackagesDirectory);
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);

            // --- Install: hackpkg downloads index.json and the package, an administrator confirms ---
            await OpenAppAsync(page, "Terminal");
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");
            await RunCommandAsync(page, screen, $"sudo hackpkg install {ProbeRepository}");
            await ConfirmAsAdministratorAsync(page);
            await WaitForTerminalTextAsync(rows, text => text.Contains("restart HackerOS to finish", StringComparison.Ordinal), "the end of the installation");
            Log("the test package was installed.");
            session.AssertNoErrors("hackpkg install");

            // --- Restart (ADR 0182, Q5 C): the app joins the catalog at boot ---
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await SignInAsync(page);
            await OpenAppAsync(page, ProbeAppName);

            ILocator status = page.Locator("[data-probe-status]");
            await Assertions.Expect(status).ToHaveTextAsync("engine ready", new() { Timeout = BootTimeoutMs });
            Log("the module, its Web Worker and the worker's importScripts loaded.");
            await page.WaitForFunctionAsync(
                "() => { const image = document.querySelector('[data-probe-image]'); return !!image && image.complete && image.naturalWidth > 0; }",
                null, new PageWaitForFunctionOptions { Timeout = StepTimeoutMs });
            Log("the image loaded.");

            string worker = await page.EvaluateAsync<string>("() => navigator.serviceWorker.controller ? navigator.serviceWorker.controller.scriptURL : ''");
            Assert.Contains("mode=installed-apps", worker, StringComparison.Ordinal);
            string type = await page.EvaluateAsync<string>($"async () => (await fetch('{ProbeContent}engine/engine.js')).headers.get('Content-Type')");
            Assert.Equal("text/javascript", type);
            session.AssertNoErrors("installed app");
            await CloseAllWindowsAsync(page);
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "installed-app");
            throw;
        }
    }

    /// <summary>
    /// The published service worker (on the address where index.html registers it) answers a file the
    /// OS copied into the installed apps' cache, before its release cache, for a page and for a Web
    /// Worker started under cross-origin isolation (ADR 0131).
    /// </summary>
    [TrimmedPublishFact]
    public async Task The_published_service_worker_serves_installed_app_files_to_a_worker()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        if (settings.PwaUrl is null)
        {
            Log($"{TrimmedPublishSettings.PwaUrlVariable} is not set: no service worker, published worker check skipped.");
            return;
        }

        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.PwaUrl);
            await WaitForServiceWorkerControllerAsync(page);
            await SignInAsync(page);

            string answer = await page.EvaluateAsync<string>(
                """
                async content => {
                    const assets = await import('./_content/HackerOs.Infrastructure.Browser/installedAppAssets.js');
                    if (!await assets.prepare(10000)) return 'no service worker';
                    const encode = text => Array.from(new TextEncoder().encode(text));
                    await assets.put(content + 'engine/engine.js', encode("self.engineAnswer = () => 'served';"), 'text/javascript');
                    await assets.put(content + 'engine/worker.js', encode("importScripts('engine.js'); self.onmessage = () => self.postMessage(self.engineAnswer());"), 'text/javascript');
                    try {
                        return await new Promise((resolve, reject) => {
                            const worker = new Worker(content + 'engine/worker.js');
                            worker.onmessage = event => { worker.terminate(); resolve(event.data); };
                            worker.onerror = () => { worker.terminate(); reject(new Error('worker failed')); };
                            worker.postMessage('ping');
                        });
                    } finally {
                        await assets.remove(content + 'engine/engine.js');
                        await assets.remove(content + 'engine/worker.js');
                    }
                }
                """, ProbeContent);

            Assert.Equal("served", answer);
            Log($"the published service worker served a worker and its importScripts (crossOriginIsolated={await page.EvaluateAsync<bool>("() => crossOriginIsolated")}).");
            session.AssertNoErrors("published service worker");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "published-worker");
            throw;
        }
    }

    /// <summary>
    /// Answers the GitHub addresses of the test package from <paramref name="directory"/>, as
    /// <c>raw.githubusercontent.com</c> would (with <c>Access-Control-Allow-Origin: *</c>).
    /// </summary>
    private async Task ServePackagesLocallyAsync(IBrowserContext context, string directory)
    {
        string prefix = $"/{ProbeRepository}/hackeros-packages/";
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        await context.RouteAsync(new Regex("^" + Regex.Escape("https://raw.githubusercontent.com" + prefix)), async route =>
        {
            string relative = Uri.UnescapeDataString(new Uri(route.Request.Url).AbsolutePath)[prefix.Length..];
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404, Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" } });
                return;
            }

            Log($"served {relative} locally.");
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                BodyBytes = await File.ReadAllBytesAsync(path),
                Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*", ["Content-Type"] = "application/octet-stream" },
            });
        });
    }

    /// <summary>
    /// Answers the administrator prompt <c>sudo</c> opens (the credential dialog of the shell, or the
    /// OS authentication agent of ADR 0055 §5) with the administrator's login and password.
    /// </summary>
    private async Task ConfirmAsAdministratorAsync(IPage page)
    {
        ILocator prompt = page.Locator(".credential-prompt-dialog, .aaa-window").First;
        await prompt.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = StepTimeoutMs });
        ILocator login = prompt.Locator("input[type=text]");
        if (await login.CountAsync() > 0 && string.IsNullOrEmpty(await login.InputValueAsync()))
        {
            await login.FillAsync(LoginName);
        }

        ILocator password = prompt.Locator("input[type=password]");
        await password.FillAsync(Password);
        await password.PressAsync("Enter");
        await prompt.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = StepTimeoutMs });
        Log("the administrator confirmed.");
    }
}
