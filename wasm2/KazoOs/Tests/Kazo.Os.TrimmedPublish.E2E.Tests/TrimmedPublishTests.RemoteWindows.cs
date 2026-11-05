using System.Text.Json;
using System.Text.Json.Nodes;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Kazo.Os.TrimmedPublish.E2E.Tests;

/// <summary>
/// The trial of window apps rendered in a Web Worker (ADR 0129), on the trimmed Release publish the
/// ADR asks the measurements on: the Calculator with the host setting on behaves and looks as it does
/// on the page, and the numbers the verdict (ADR 0133) rests on are measured in both modes.
/// </summary>
public sealed partial class TrimmedPublishTests
{
    private const string CalculatorId = "org.kazoos.calculator";

    /// <summary>
    /// With <c>KazoOs:RemoteWindowRendering</c> on: the Calculator's window content comes from a
    /// worker, computes by mouse and keyboard, keeps its isolated CSS, moves the focus with Tab, has no
    /// serious axe finding, obeys the window manager's shortcuts, and frees its worker when closed.
    /// </summary>
    [TrimmedPublishFact]
    public async Task The_calculator_rendered_in_a_worker_works_like_on_the_page()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await EnableRemoteWindowsAsync(page, enabled: true);
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);
            await WaitForReadyWorkerAsync(page);

            await OpenAppAsync(page, "Calculator");
            ILocator host = page.Locator(".remote-window-host[data-remote-window='running']");
            await host.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
            ILocator display = page.Locator("#calc-display-current");
            await display.WaitForAsync();
            Log($"the Calculator renders in worker {await host.GetAttributeAsync("data-worker")}.");

            // Mouse: 7 + 8 = 15, through DOM events sent to the worker.
            foreach (string button in new[] { "calc-7", "calc-add", "calc-8", "calc-eq" })
            {
                await page.Locator("#" + button).ClickAsync();
            }

            await Assertions.Expect(display).ToHaveTextAsync("15");

            // Keyboard: the root's @onkeydown in the worker.
            await page.Locator(".calculator-root").FocusAsync();
            await page.Keyboard.TypeAsync("12*3");
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(display).ToHaveTextAsync("36");

            // Isolated CSS: the scope attribute is set and the app's own rules apply.
            string background = await page.Locator("#calc-eq").EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor");
            Assert.NotEqual("rgba(0, 0, 0, 0)", background);

            // Tab moves the focus from the root to the first key, natively, on the page.
            await page.Locator(".calculator-root").FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            Assert.Equal("calc-mc", await page.EvaluateAsync<string>("() => document.activeElement?.id ?? ''"));

            // Accessibility: no serious or critical axe finding in the window.
            AxeResult axe = await page.RunAxe(new AxeRunContext { Include = [new AxeSelector("article.window-host")] });
            AxeResultItem[] serious = [.. axe.Violations.Where(v => v.Impact is "serious" or "critical")];
            Assert.True(serious.Length == 0, "axe: " + string.Join("; ", serious.Select(v => $"{v.Id}: {v.Help}")));

            // The window manager's shortcuts (ADR 0095) act on the page's chrome: Alt+F7 moves, Alt+Q closes.
            ILocator window = page.Locator("article.window-host.is-focused");
            double left = await window.EvaluateAsync<double>("e => e.getBoundingClientRect().left");
            await page.Keyboard.PressAsync("Alt+F7");
            for (int step = 0; step < 5; step++)
            {
                await page.Keyboard.PressAsync("ArrowRight");
            }

            await page.Keyboard.PressAsync("Enter");
            await page.WaitForTimeoutAsync(300);
            Assert.True(await window.EvaluateAsync<double>("e => e.getBoundingClientRect().left") > left, "Alt+F7 did not move the window.");
            await page.Keyboard.PressAsync("Alt+KeyQ");
            await Assertions.Expect(page.Locator("[data-window-id]")).ToHaveCountAsync(0);

            // The window's process ended: its dedicated worker is gone.
            await OpenAppAsync(page, "Terminal");
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");
            await RunCommandAsync(page, screen, "clear; cat /proc/kazoos/workers");
            string workers = await WaitForTerminalTextAsync(rows, text => text.Contains("pool_size", StringComparison.Ordinal), "/proc/kazoos/workers");
            Assert.DoesNotContain("dedicated", workers, StringComparison.Ordinal);
            session.AssertNoErrors("calculator in a worker");
            Log("the Calculator worked in a worker; its worker was freed on close.");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "remote-window");
            throw;
        }
    }

    /// <summary>
    /// The measurements of ADR 0129 section 3, on the page (setting off) then in a worker (on): opening
    /// time, click-to-DOM and click-to-next-frame latency, worker and page memory, batch sizes. Written
    /// to <c>remote-window-measurements.json</c> in the artifacts folder and to the test output; the
    /// verdict compares them to the thresholds of section 4 (ADR 0133).
    /// </summary>
    [TrimmedPublishFact]
    public async Task Measures_the_calculator_on_the_page_and_in_a_worker()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        JsonObject report = new()
        {
            ["page"] = await MeasureAsync(settings, remote: false),
            ["worker"] = await MeasureAsync(settings, remote: true)
        };
        string json = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Log("measurements:\n" + json);
        if (settings.ArtifactsDirectory is { } artifacts)
        {
            Directory.CreateDirectory(artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, "remote-window-measurements.json"), json);
        }

        Assert.True(report["worker"]!["latency"]!["samples"]!.GetValue<int>() > 0);
        Assert.True(report["worker"]!["batches"]!["count"]!.GetValue<int>() > 0);
    }

    private async Task<JsonObject> MeasureAsync(TrimmedPublishSettings settings, bool remote)
    {
        const int Opens = 6;
        const int Clicks = 300;
        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        await EnableRemoteWindowsAsync(page, remote);
        await NavigateWhenReadyAsync(page, settings.Url);
        await SignInAsync(page);
        await WaitForReadyWorkerAsync(page);

        JsonObject result = new() { ["mode"] = remote ? "worker" : "page" };
        result["idleWorkerMemoryBytes"] = JsonNode.Parse(JsonSerializer.Serialize(await WorkerHeapsAsync(page)));
        result["pageMemoryBytes"] = await page.EvaluateAsync<double>(HeapScript);

        // Opening: from the launch click to the display's first value, the ready worker already up
        // (ADR 0071). The first open also loads the Calculator's assembly, on the page or in the worker.
        JsonArray opens = [];
        for (int open = 0; open < Opens; open++)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" }).FillAsync("Calculator");
            await page.GetByRole(AriaRole.Button, new() { Name = "Launch Calculator", Exact = true }).WaitForAsync();
            opens.Add(Math.Round(await page.EvaluateAsync<double>(OpenScript)));
            if (open < Opens - 1)
            {
                await page.WaitForTimeoutAsync(500);
                await CloseAllWindowsAsync(page);
                await WaitForReadyWorkerAsync(page);
            }
        }

        result["opensMs"] = opens;
        await page.WaitForTimeoutAsync(1_500);
        result["workerMemoryBytesWithWindow"] = JsonNode.Parse(JsonSerializer.Serialize(await WorkerHeapsAsync(page)));
        ILocator remoteHost = page.Locator(".remote-window-host");
        result["windowWorker"] = await remoteHost.CountAsync() > 0 ? await remoteHost.GetAttributeAsync("data-worker") : null;
        result["latency"] = JsonNode.Parse(await page.EvaluateAsync<string>(LatencyScript, Clicks));
        result["batches"] = JsonNode.Parse(await page.EvaluateAsync<string>(BatchScript));
        session.AssertNoErrors($"measurements ({result["mode"]})");
        return result;
    }

    /// <summary>Turns the trial on or off for this page by rewriting <c>appsettings.json</c> on the way in.</summary>
    private static Task EnableRemoteWindowsAsync(IPage page, bool enabled) =>
        page.RouteAsync("**/appsettings.json", async route =>
        {
            IAPIResponse response = await route.FetchAsync();
            JsonNode settings = JsonNode.Parse(await response.TextAsync())!;
            settings["KazoOs"]!["RemoteWindowRendering"] = new JsonObject
            {
                ["Enabled"] = enabled,
                ["Apps"] = new JsonArray(CalculatorId)
            };
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Response = response,
                Body = settings.ToJsonString(),
                ContentType = "application/json"
            });
        });

    /// <summary>Waits for the always-ready worker (ADR 0071): it starts once the desktop is idle.</summary>
    private static async Task WaitForReadyWorkerAsync(IPage page)
    {
        for (int attempt = 0; attempt < 60 && page.Workers.Count < 2; attempt++)
        {
            await Task.Delay(250);
        }

        // The ready worker's runtime takes about a second to answer "hello" after it was created.
        await Task.Delay(3_000);
    }

    /// <summary>The WebAssembly memory of each worker's .NET runtime (runtime, assemblies, heaps).</summary>
    private static async Task<List<double>> WorkerHeapsAsync(IPage page)
    {
        List<double> heaps = [];
        foreach (IWorker worker in page.Workers)
        {
            try
            {
                heaps.Add(await worker.EvaluateAsync<double>(HeapScript));
            }
            catch (PlaywrightException)
            {
                // A worker that went away between the listing and the call.
            }
        }

        return heaps;
    }

    private const string HeapScript = "() => globalThis.getDotnetRuntime?.(0)?.Module?.HEAPU8?.length ?? -1";

    private const string OpenScript = """
        async () => {
            const t0 = performance.now();
            document.querySelector('[aria-label="Launch Calculator"]').click();
            await new Promise(resolve => {
                const check = () => {
                    const display = document.querySelector('#calc-display-current');
                    if (display && display.textContent.trim().length > 0) { resolve(); return; }
                    requestAnimationFrame(check);
                };
                check();
            });
            return performance.now() - t0;
        }
        """;

    /// <summary>
    /// A digit key clicked (a real DOM click event), then the time until the display's text changed
    /// (DOM) and until the next animation frame after it (the image the user sees).
    /// </summary>
    private const string LatencyScript = """
        async count => {
            const display = document.querySelector('#calc-display-current');
            const dom = [], frame = [];
            const digits = ['1', '2', '3', '4', '5', '6', '7', '8', '9'];
            for (let i = 0; i < count; i++) {
                const id = i % 8 === 7 ? 'calc-ac' : 'calc-' + digits[i % 9];
                const before = display.textContent;
                if (id === 'calc-ac' && before.trim() === '0') continue;
                await new Promise(r => setTimeout(r, 30));
                const button = document.getElementById(id);
                const t0 = performance.now();
                await new Promise(resolve => {
                    const done = () => {
                        const t1 = performance.now();
                        requestAnimationFrame(() => { dom.push(t1 - t0); frame.push(performance.now() - t0); resolve(); });
                    };
                    const observer = new MutationObserver(() => {
                        if (display.textContent === before) return;
                        observer.disconnect();
                        done();
                    });
                    observer.observe(display, { childList: true, characterData: true, subtree: true });
                    button.click();
                    if (display.textContent !== before) { observer.disconnect(); done(); }
                });
            }
            const pct = (a, p) => { const s = [...a].sort((x, y) => x - y); return +s[Math.min(s.length - 1, Math.floor(p * s.length))].toFixed(2); };
            return JSON.stringify({ samples: dom.length, domP50: pct(dom, 0.5), domP95: pct(dom, 0.95), domMax: pct(dom, 1),
                frameP50: pct(frame, 0.5), frameP95: pct(frame, 0.95) });
        }
        """;

    private const string BatchScript = """
        () => {
            const s = document.querySelector('.remote-window-host')?.__kazoosRemoteWindow;
            return JSON.stringify(s ? { count: s.batches, bytes: s.bytes, firstBytes: s.firstBytes, averageBytes: Math.round(s.bytes / s.batches),
                operations: s.operations, averageEncodeMicroseconds: Math.round(s.workerMicroseconds / s.batches), nodes: s.nodes }
                : { count: 0 });
        }
        """;
}
