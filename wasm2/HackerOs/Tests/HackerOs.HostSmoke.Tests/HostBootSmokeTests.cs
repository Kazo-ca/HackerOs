using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.HostSmoke.Tests;

/// <summary>
/// The boot smoke test every <c>rebuild/5-*</c> PR runs against the three hosts
/// (execution-isolation-plan.md section 5.2): sign-in screen, desktop, terminal with <c>ps</c>
/// and <c>cat /proc/self/status</c>, task manager, then everything closed again, with no
/// console error at any point.
/// </summary>
/// <remarks>
/// The host is already running (see <see cref="HostSmokeFactAttribute"/>); the test only drives
/// a fresh, isolated browser context, so the first visit always lands on first-run onboarding
/// for a WebAssembly host. A reload then checks the real sign-in screen.
/// </remarks>
public sealed partial class HostBootSmokeTests(ITestOutputHelper output)
{
    private const string LoginName = "smoke";
    private const string DisplayName = "Smoke Test";
    private const string Password = "Smoke-Test-1";

    /// <summary>Generous: the first visit downloads and starts the whole .NET WebAssembly runtime.</summary>
    private const float BootTimeoutMs = 120_000;
    private const float StepTimeoutMs = 30_000;

    /// <summary>stress allocates several hundred MiB in 1 MiB blocks before the OOM killer's two samples.</summary>
    private const float OomTimeoutMs = 120_000;

    /// <summary>Boots the host to the desktop, runs the terminal and the task manager, closes everything.</summary>
    [HostSmokeFact]
    public async Task Host_boots_to_the_desktop_and_runs_the_terminal_and_task_manager()
    {
        HostSmokeSettings settings = HostSmokeSettings.FromEnvironment()!;
        Log($"host '{settings.HostName}' at {settings.Url}");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(
            playwright, new BrowserTypeLaunchOptions { Headless = true });
        await using IBrowserContext context = await browser.NewContextAsync(
            new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
        IPage page = await context.NewPageAsync();
        page.SetDefaultTimeout(StepTimeoutMs);
        List<string> errors = AttachDiagnostics(page);

        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page, firstVisit: true);
            AssertNoErrors(errors, "first sign-in");

            // Back to the sign-in screen: a reload ends the browser-side session as a power cycle would.
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await SignInAsync(page, firstVisit: false);
            AssertNoErrors(errors, "sign-in after reload");
            await AssertScopedStylesAppliedAsync(page, ".taskbar-container", "taskbar (HackerOs.Taskbar.Blazor)");
            await CheckServerConnectionAsync(page, settings);
            AssertNoErrors(errors, "server connection");

            await OpenAppAsync(page, "Terminal");
            // Read the rendered rows, but click the screen: xterm.js stacks it over the rows.
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");
            await AssertScopedStylesAppliedAsync(page, ".window-chrome", "window chrome (HackerOs.Windowing.Blazor)");

            await RunCommandAsync(page, screen, "ps");
            string psOutput = await WaitForTerminalTextAsync(
                rows, text => OutputFinished(text, "PID"), "the ps output and the next prompt");
            Assert.Contains("hsh", psOutput, StringComparison.Ordinal);
            Log("ps listed the processes.");

            await RunCommandAsync(page, screen, "cat /proc/self/status");
            string status = await WaitForTerminalTextAsync(
                rows, text => OutputFinished(text, "Pid:") && text.Contains("VmRSS:", StringComparison.Ordinal),
                "/proc/self/status and the next prompt");
            Assert.Contains("Name:", status, StringComparison.Ordinal);
            AssertSource(status, "CpuSource", settings.ExpectedCpuSource);
            AssertSource(status, "MemSource", settings.ExpectedMemSource);
            Log($"/proc/self/status read ({Sources(status)}).");
            AssertNoErrors(errors, "terminal");

            await CheckWorkersAsync(page, screen, rows, settings);
            AssertNoErrors(errors, "workers");

            await CheckIsolationAsync(page, screen, rows, settings);
            AssertNoErrors(errors, "cross-origin isolation");

            await CheckLimitsAsync(page, screen, rows, settings);
            AssertNoErrors(errors, "limits");

            if (settings.ExpectsOomKill)
            {
                await CheckOomKillerAsync(page, screen, rows);
                AssertNoErrors(errors, "OOM killer");
            }

            await OpenAppAsync(page, "System Monitor");
            ILocator filter = page.GetByLabel("Filter processes");
            await filter.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await filter.FillAsync("terminal");
            await page.Locator("table.proc-table tbody tr").First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Log("task manager lists the terminal process.");
            AssertNoErrors(errors, "task manager");

            await CloseAllWindowsAsync(page);
            AssertNoErrors(errors, "closing every window");
            Log("all windows closed; no console error.");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings);
            throw;
        }
    }

    /// <summary>
    /// ADR 0134: the OS served by <c>HackerOs.Server</c> connects to it without any question, and
    /// the notification-area icon says so ("Connected, not identified"); identifying from the icon's
    /// panel (a new account) turns it to "Connected". A host that is not a Kazo OS server shows no
    /// server icon at all.
    /// </summary>
    private async Task CheckServerConnectionAsync(IPage page, HostSmokeSettings settings)
    {
        ILocator icon = page.Locator(".server-status-trigger");
        if (!settings.ExpectsServerConnection)
        {
            // The discovery answers at once on a static host; give it a moment, then nothing may show.
            await Task.Delay(2_000);
            Assert.Equal(0, await icon.CountAsync());
            Log("no server icon on this host.");
            return;
        }

        await page.Locator(".server-status-trigger.is-anonymous").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Log($"server icon: {await icon.GetAttributeAsync("aria-label")}");

        await icon.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Identify" }).ClickAsync();
        await page.GetByLabel("Account name").FillAsync($"{LoginName}-{Guid.NewGuid():N}"[..24]);
        await page.Locator("#server-status-password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create an account" }).ClickAsync();
        await page.Locator(".server-status-trigger.is-connected").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Log($"identified from the icon: {await icon.GetAttributeAsync("aria-label")}");
        await icon.ClickAsync(); // close the panel
    }

    /// <summary>
    /// ADR 0070/0071 (plan section 5.2): on every host, commands run in Web Workers — a pipeline
    /// works, <c>/proc/hackeros/workers</c> lists the pool, and a command stuck in a loop
    /// (<c>stress</c>) leaves the desktop responsive, shows "not-responding" and dies on
    /// <c>kill -9</c> and on <c>kill</c> (escalation after the grace period). A host without
    /// workers (none since ADR 0130) has no such file and commands still run.
    /// </summary>
    private async Task CheckWorkersAsync(IPage page, ILocator screen, ILocator rows, HostSmokeSettings settings)
    {
        await RunCommandAsync(page, screen, "clear; seq 30 | grep 1 | wc -l");
        await WaitForTerminalTextAsync(rows, text => Regex.IsMatch(text, @"^\s*12\s*$", RegexOptions.Multiline), "the pipeline's count");
        Log("pipeline ran.");

        // ADR 0124, section 4: a command of a sample project, in an assembly of its own that no worker
        // preloads; a pool worker fetches it on demand (the server host serves the same OS, ADR 0130).
        await RunCommandAsync(page, screen, "clear; sample-cmd loaded-on-demand");
        await WaitForTerminalTextAsync(rows, text => Regex.IsMatch(text, @"^loaded-on-demand\s*$", RegexOptions.Multiline), "the sample command's output");
        Log("sample-cmd ran from its own assembly.");

        await RunCommandAsync(page, screen, "clear; cat /proc/hackeros/workers");
        if (!settings.ExpectsWorkers)
        {
            await WaitForTerminalTextAsync(rows, text => text.Contains("No such file", StringComparison.Ordinal), "no worker on this host");
            Log("no Web Worker on this host (ADR 0070, section 6).");
            return;
        }

        string workers = await WaitForTerminalTextAsync(
            rows, text => text.Contains("pool_size", StringComparison.Ordinal) && Regex.IsMatch(text, @"^\d+\s+pool\s", RegexOptions.Multiline),
            "/proc/hackeros/workers with a pool worker");
        Log($"workers:\n{workers.Trim()}");

        // A command that never yields, in the background: the desktop must keep answering.
        await RunCommandAsync(page, screen, "clear; stress -t 600 &");
        await WaitForTerminalTextAsync(rows, text => text.Contains("dispatching hogs", StringComparison.Ordinal), "stress started");
        Stopwatch answered = Stopwatch.StartNew();
        await RunCommandAsync(page, screen, "echo still-responsive");
        await WaitForTerminalTextAsync(rows, text => Regex.IsMatch(text, @"^still-responsive\s*$", RegexOptions.Multiline), "an echo while stress spins");
        Log($"echo answered in {answered.ElapsedMilliseconds} ms while stress spins.");
        await Task.Delay(6_500); // past the 5 s unresponsive threshold
        await RunCommandAsync(page, screen, "clear; cat /proc/hackeros/workers");
        await WaitForTerminalTextAsync(rows, text => text.Contains("not-responding", StringComparison.Ordinal), "a not-responding worker");
        await RunCommandAsync(page, screen, "clear; kill -9 %1");
        await WaitForTerminalTextAsync(rows, text => text.Contains("Killed", StringComparison.Ordinal), "Killed after kill -9");
        Log("kill -9 ended the stuck command.");

        // SIGTERM is never delivered to a stuck worker: the OS terminates it after the grace period.
        await RunCommandAsync(page, screen, "clear; stress -t 600 &");
        await WaitForTerminalTextAsync(rows, text => text.Contains("dispatching hogs", StringComparison.Ordinal), "stress started again");
        await RunCommandAsync(page, screen, "kill %1; sleep 7; jobs");
        await WaitForTerminalTextAsync(
            rows, text => text.Contains("Terminated", StringComparison.Ordinal) || text.Contains("Killed", StringComparison.Ordinal),
            "the job ended after kill");
        Log("kill (SIGTERM) ended the stuck command after the grace period.");
    }

    /// <summary>
    /// ADR 0131 (adjusted by ADR 0143): the page is cross-origin isolated exactly when the run expects
    /// it, <c>/proc/hackeros/workers</c> says so on its first line, and what isolation adds follows:
    /// isolated, a command in a worker lists processes synchronously and a long pipe through the
    /// shared rings keeps every line; not isolated, the synchronous call is refused and the pipe
    /// still works over messages.
    /// </summary>
    private async Task CheckIsolationAsync(IPage page, ILocator screen, ILocator rows, HostSmokeSettings settings)
    {
        if (settings.ExpectedIsolation is not { } expected)
        {
            return;
        }

        bool isolated = await page.EvaluateAsync<bool>("() => globalThis.crossOriginIsolated === true");
        Assert.True(isolated == expected, $"crossOriginIsolated is {isolated}, the run expects {expected}.");
        string answer = expected ? "yes" : "no";
        await RunCommandAsync(page, screen, "clear; head -3 /proc/hackeros/workers");
        string header = await WaitForTerminalTextAsync(rows, text => OutputFinished(text, "isolation-setting:"), "the isolation lines of /proc/hackeros/workers");
        Assert.Contains($"cross-origin-isolated: {answer}", header, StringComparison.Ordinal);
        Log($"isolation:\n{header.Trim()}");

        await RunCommandAsync(page, screen, "clear; sample-cmd --processes");
        if (expected)
        {
            await WaitForTerminalTextAsync(rows, text => Regex.IsMatch(text, @"^processes: \d", RegexOptions.Multiline), "the synchronous process list");
            Log("sample-cmd --processes: synchronous call answered.");
        }
        else
        {
            await WaitForTerminalTextAsync(rows, text => text.Contains("ListProcesses", StringComparison.Ordinal), "the refused synchronous call");
            Log("sample-cmd --processes: synchronous call refused without isolation.");
        }

        await RunCommandAsync(page, screen, "clear; seq 30000 | wc -l");
        await WaitForTerminalTextAsync(rows, text => Regex.IsMatch(text, @"^\s*30000\s*$", RegexOptions.Multiline), "every line of the long pipe");
        Log("seq 30000 | wc -l: 30000.");
    }

    /// <summary>
    /// The <c>exit=N</c> line the CPU-limit step prints; the typed command line itself shows
    /// <c>exit=$?</c>, which must not count.
    /// </summary>
    private static bool ExitStatusShown(string text) => ExitStatusPattern().IsMatch(text);

    [GeneratedRegex(@"exit=\d")]
    private static partial Regex ExitStatusPattern();

    /// <summary>
    /// ADR 0125: <c>/proc/self/limits</c> says how limits are enforced on this host, and the CPU
    /// limit ends a command with <c>SIGXCPU</c> (exit 152) where CPU time is measured, strictly in a
    /// Web Worker. Where it is not measured the limit is inactive and the command ends normally; with
    /// limits switched off, <c>ulimit</c> cannot change anything.
    /// </summary>
    private async Task CheckLimitsAsync(IPage page, ILocator screen, ILocator rows, HostSmokeSettings settings)
    {
        await RunCommandAsync(page, screen, "clear; cat /proc/self/limits");
        string limits = await WaitForTerminalTextAsync(
            rows, text => OutputFinished(text, "# enforcement:"), "/proc/self/limits and the next prompt");
        string expected = !settings.LimitsEnabled ? "# enforcement: off"
            : settings.ExpectsWorkers ? "# enforcement: strict (worker)"
            : "# enforcement: best-effort (main thread)";
        Assert.Contains(expected, limits, StringComparison.Ordinal);
        Log($"/proc/self/limits: {expected[2..]}.");

        if (!settings.LimitsEnabled)
        {
            await RunCommandAsync(page, screen, "clear; ulimit -n 100");
            await WaitForTerminalTextAsync(rows, text => text.Contains("cannot modify limit", StringComparison.Ordinal), "ulimit refused");
            Log("limits off: ulimit cannot change anything.");
            return;
        }

        // The child shell gets the limit, stress inherits it; the terminal's own shell keeps its own.
        bool cpuMeasured = string.Equals(settings.ExpectedCpuSource, "measured", StringComparison.Ordinal);
        await RunCommandAsync(page, screen, $"clear; sh -c 'ulimit -S -t 1; stress --yield -t {(cpuMeasured ? 30 : 2)}'; echo \"exit=$?\"");
        if (cpuMeasured)
        {
            string ended = await WaitForTerminalTextAsync(rows, ExitStatusShown, "the end of the CPU-limited stress");
            Assert.Contains("CPU time limit exceeded", ended, StringComparison.Ordinal);
            Assert.Contains("exit=152", ended, StringComparison.Ordinal);
            Log("ulimit -S -t 1; stress --yield: SIGXCPU, exit 152.");
        }
        else
        {
            string ended = await WaitForTerminalTextAsync(rows, ExitStatusShown, "the end of stress");
            Assert.Contains("exit=0", ended, StringComparison.Ordinal);
            Log("CPU not measured: the CPU limit is inactive, stress ran to its end.");
        }
    }

    /// <summary>
    /// ADR 0126: on a machine with a small simulated <c>MemTotal</c>, an administrator turns the OOM
    /// killer on (<c>sudo tee /etc/hackeros/memory.config</c>), <c>stress --vm</c> allocates more than
    /// the free memory, and the kernel kills it: the shell prints <c>Killed</c> (exit 137),
    /// <c>dmesg</c> shows the <c>Out of memory</c> line and <c>/proc/vmstat</c> counts the kill.
    /// </summary>
    private async Task CheckOomKillerAsync(IPage page, ILocator screen, ILocator rows)
    {
        await RunCommandAsync(page, screen,
            "clear; printf 'schemaVersion=1\\n[oom-killer]\\nenabled=true\\nmin-free-percent=5\\n' | sudo tee /etc/hackeros/memory.config");
        ILocator dialog = page.Locator(".credential-prompt-dialog");
        await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await dialog.Locator("input[type=text]").FillAsync(LoginName);
        await dialog.Locator("input[type=password]").FillAsync(Password);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Authorize" }).ClickAsync();
        await WaitForTerminalTextAsync(rows, text => OutputFinished(text, "min-free-percent=5"), "memory.config written by sudo tee");

        await RunCommandAsync(page, screen, "clear; dmesg | grep oom-killer");
        await WaitForTerminalTextAsync(rows, text => OutputFinished(text, "oom-killer: enabled, min-free-percent=5"), "the kernel line of the enabled OOM killer");
        Log("OOM killer enabled through sudo tee; dmesg says so.");

        await RunCommandAsync(page, screen, "clear; grep -E 'MemTotal|MemAvailable' /proc/meminfo");
        string meminfo = await WaitForTerminalTextAsync(rows, text => OutputFinished(text, "MemAvailable:"), "/proc/meminfo");
        long total = MemInfoKilobytes(meminfo, "MemTotal");
        long available = MemInfoKilobytes(meminfo, "MemAvailable");
        // Enough to bring MemAvailable well under 5 % of MemTotal.
        long mebibytes = Math.Max(16, (available - (total * 5 / 100)) / 1024 + 64);
        Log($"MemTotal {total} kB, MemAvailable {available} kB: stress --vm 1 --vm-bytes {mebibytes}M.");

        await RunCommandAsync(page, screen, $"clear; stress --vm 1 --vm-bytes {mebibytes}M --vm-keep -t 120; echo \"exit=$?\"");
        string ended = await WaitForTerminalTextAsync(rows, ExitStatusShown, "the OOM kill of stress", OomTimeoutMs);
        Assert.Contains("exit=137", ended, StringComparison.Ordinal);
        Assert.Contains("Killed", ended, StringComparison.Ordinal);
        Log("stress --vm: killed by the OOM killer, exit 137.");

        await RunCommandAsync(page, screen, "clear; dmesg | grep 'Out of memory'");
        string kernel = await WaitForTerminalTextAsync(rows, text => OutputFinished(text, "Out of memory: Killed process"), "the Out of memory line of dmesg");
        Assert.Contains("(stress)", kernel, StringComparison.Ordinal);

        await RunCommandAsync(page, screen, "clear; grep oom_kill /proc/vmstat");
        await WaitForTerminalTextAsync(rows, text => OutputFinished(text, "oom_kill 1"), "oom_kill 1 in /proc/vmstat");
        Log("dmesg: Out of memory: Killed process (stress); /proc/vmstat: oom_kill 1.");
    }

    private static long MemInfoKilobytes(string meminfo, string key)
    {
        Match match = Regex.Match(meminfo, key + @":\s+(\d+) kB");
        Assert.True(match.Success, $"{key} not found in /proc/meminfo:\n{meminfo}");
        return long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Completes onboarding (first visit) or the sign-in form, then waits for the desktop. A host
    /// that keeps its session across a reload may go straight to the desktop; that is accepted.
    /// </summary>
    private async Task SignInAsync(IPage page, bool firstVisit)
    {
        ILocator onboarding = page.GetByRole(AriaRole.Heading, new() { Name = "Create administrator" });
        ILocator signIn = page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" });
        ILocator launcher = page.GetByRole(AriaRole.Button, new() { Name = "App launcher" });
        ILocator fatal = page.Locator(".fatal-surface, .recovery-surface");

        await onboarding.Or(signIn).Or(launcher).Or(fatal).First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = BootTimeoutMs });

        if (await fatal.IsVisibleAsync())
        {
            throw new InvalidOperationException($"The host stopped on an error screen: {await fatal.InnerTextAsync()}");
        }

        if (await onboarding.IsVisibleAsync())
        {
            Log("first-run onboarding shown.");
            await page.GetByLabel("Login name").FillAsync(LoginName);
            await page.GetByLabel("Display name").FillAsync(DisplayName);
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
            await page.GetByLabel("Confirm password").FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Create local profile" }).ClickAsync();
        }
        else if (await signIn.IsVisibleAsync())
        {
            Log("sign-in screen shown.");
            ILocator user = page.GetByLabel("Local user");
            if (await user.Locator($"option[value='{LoginName}']").CountAsync() > 0)
            {
                await user.SelectOptionAsync(LoginName);
            }

            await page.GetByLabel("Password").FillAsync(Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
        }
        else if (firstVisit)
        {
            Log("desktop shown without a sign-in step.");
        }

        await launcher.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = BootTimeoutMs });
        Log("desktop ready.");
    }

    /// <summary>Opens an app from the Start menu by its exact display name.</summary>
    private async Task OpenAppAsync(IPage page, string appName)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).ClickAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Search applications" }).FillAsync(appName);
        ILocator launch = page.GetByRole(AriaRole.Button, new() { Name = $"Launch {appName}", Exact = true });
        await launch.ClickAsync();
        Log($"launched '{appName}'.");
    }

    /// <summary>Types a command line into the xterm.js screen, as a person would.</summary>
    private static async Task RunCommandAsync(IPage page, ILocator screen, string commandLine)
    {
        await screen.ClickAsync();
        await page.Keyboard.TypeAsync(commandLine);
        await page.Keyboard.PressAsync("Enter");
    }

    /// <summary>Polls the terminal's rendered rows until <paramref name="done"/> holds.</summary>
    private async Task<string> WaitForTerminalTextAsync(ILocator rows, Func<string, bool> done, string what, float timeoutMs = StepTimeoutMs)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string text = string.Empty;
        while (elapsed.ElapsedMilliseconds < timeoutMs)
        {
            text = (await rows.InnerTextAsync()).Replace(' ', ' ');
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

    /// <summary>
    /// Fails when a component's scoped CSS (CSS isolation) did not reach the page. The probed
    /// elements are a <c>&lt;footer&gt;</c> and a <c>&lt;header&gt;</c>, <c>display: block</c> by
    /// default, that only their scoped stylesheet turns into <c>flex</c>: a host that forgets its
    /// <c>{Project}.styles.css</c> bundle (HackerOs.Server did) renders an unstyled desktop
    /// without any console error.
    /// </summary>
    private async Task AssertScopedStylesAppliedAsync(IPage page, string selector, string what)
    {
        ILocator element = page.Locator(selector).First;
        await element.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        string display = await element.EvaluateAsync<string>("e => getComputedStyle(e).display");
        Assert.True(display == "flex", $"Scoped styles of the {what} are not applied: '{selector}' has display '{display}', expected 'flex'.");
        Log($"scoped styles applied: {what}.");
    }

    private static void AssertSource(string status, string field, string? expected)
    {
        if (expected is null)
        {
            return;
        }

        Match match = Regex.Match(status, $@"{field}:\s*(\w+)");
        Assert.True(match.Success, $"/proc/self/status has no '{field}:' line.");
        Assert.Equal(expected, match.Groups[1].Value);
    }

    /// <summary>The <c>CpuSource:</c>/<c>MemSource:</c> values for the log, when present.</summary>
    private static string Sources(string status)
    {
        Match cpu = Regex.Match(status, @"CpuSource:\s*(\w+)");
        Match mem = Regex.Match(status, @"MemSource:\s*(\w+)");
        return $"CpuSource {(cpu.Success ? cpu.Groups[1].Value : "-")}, MemSource {(mem.Success ? mem.Groups[1].Value : "-")}";
    }

    private static void AssertNoErrors(List<string> errors, string step)
    {
        lock (errors)
        {
            if (errors.Count > 0)
            {
                Assert.Fail($"Console errors by the end of '{step}':\n  {string.Join("\n  ", errors)}");
            }
        }
    }

    /// <summary>Records console errors and uncaught page errors; logs everything else for diagnosis.</summary>
    private List<string> AttachDiagnostics(IPage page)
    {
        List<string> errors = [];
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                lock (errors)
                {
                    errors.Add($"console: {message.Text}");
                }
            }

            if (message.Type is "error" or "warning")
            {
                Log($"[console:{message.Type}] {message.Text}");
            }
        };
        page.PageError += (_, error) =>
        {
            lock (errors)
            {
                errors.Add($"page error: {error}");
            }

            Log($"[pageerror] {error}");
        };
        page.RequestFailed += (_, request) => Log($"[requestfailed] {request.Method} {request.Url} — {request.Failure}");
        // The console only says "Failed to load resource: 404"; the response names the URL.
        // (Chromium reports no event for its implicit /favicon.ico request.)
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                Log($"[http {response.Status}] {response.Request.Method} {response.Url}");
            }
        };
        return errors;
    }

    /// <summary>Retries the first navigation while the host is still binding its port.</summary>
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

    private async Task SaveScreenshotAsync(IPage page, HostSmokeSettings settings)
    {
        if (settings.ArtifactsDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(settings.ArtifactsDirectory);
            string path = Path.Combine(settings.ArtifactsDirectory, $"{settings.HostName}-failure.png");
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
        // One prefix per line: verify-hosts.sh keeps only the lines that carry it.
        foreach (string line in message.Split('\n'))
        {
            output.WriteLine($"[smoke] {line}");
        }
    }

    /// <summary>The end of an hsh prompt: <c>$</c> or <c>#</c> at the end of a rendered line.</summary>
    /// <summary>
    /// True once <paramref name="marker"/> is on screen and a shell prompt follows it. A command's
    /// output reaches xterm.js in several writes (from a Web Worker, one message per write), so a
    /// marker from the first line does not mean the rest is rendered; the next prompt does.
    /// </summary>
    private static bool OutputFinished(string text, string marker)
    {
        int at = text.LastIndexOf(marker, StringComparison.Ordinal);
        return at >= 0 && PromptPattern().IsMatch(text[(at + marker.Length)..]);
    }

    [GeneratedRegex(@"[$#]\s*$", RegexOptions.Multiline)]
    private static partial Regex PromptPattern();
}
