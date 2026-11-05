using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// Boots the current OS over the IndexedDB database an older build left and checks that the
/// rename migration (ADR 0230) and the schema upgrade keep files, accounts and settings
/// (ADR 0099 section 4).
/// </summary>
/// <remarks>
/// <para>
/// Each fixture under <c>Tests/fixtures/indexeddb/</c> is a dump of the database written by the
/// real OS of an old commit (procedure: <c>doc/en/wasm/dev/building-and-testing.md</c>). The test recreates
/// that database with raw <c>indexedDB.open(name, oldVersion)</c> from a page of the harness
/// origin that does not boot Blazor, so the old database exists, at its old version, before the
/// current OS opens it. Every fixture was recorded before the rename, under the database name
/// <c>hackeros</c>: the OS first moves it to <c>kazoos</c> with the Kazo OS names (ADR 0230), then
/// opens it with <c>KazoOsIndexedDbSchema.CurrentVersion</c> and runs its migration plan, exactly as
/// on a returning user's browser.
/// </para>
/// <para>
/// Then it signs in with the fixture's administrator password (the account and its credential
/// survived), reads the fixture's file back with <c>cat</c> in the Terminal (filesystem entries,
/// links and content chunks survived), reads <c>/etc/kazoos/appearance.config</c> (the setting
/// stored in the retired <c>settings</c> store was carried over by the settings-to-files
/// migration, ADR 0044), checks the database is now <c>kazoos</c> at version 4, that <c>hackeros</c>
/// is gone, and that no stored record names HackerOS any more (the fixture's own document, whose
/// text is the person's, is stored as base64 and kept as it is).
/// </para>
/// </remarks>
public sealed partial class IndexedDbUpgradeFixtureTests(ITestOutputHelper output)
{
    /// <summary>The schema version the current OS must leave the database at.</summary>
    private const int ExpectedCurrentVersion = 4;

    /// <summary>The OS database since the rename (<c>KazoOsIndexedDbSchema.DatabaseName</c>).</summary>
    private const string CurrentDatabaseName = "kazoos";

    /// <summary>Any old name a migrated record must no longer hold.</summary>
    private static readonly Regex OldName = new("hacker ?os|hackweb|hack-?paint", RegexOptions.IgnoreCase);

    /// <summary>
    /// A same-origin page that is not the OS, answered by Playwright itself (it never reaches the
    /// harness). Opening it runs no Blazor, so nothing touches IndexedDB while the old database
    /// is recreated; its inline empty icon spares Chromium's implicit <c>/favicon.ico</c> 404.
    /// </summary>
    private const string SeedPagePath = "/__indexeddb-upgrade-fixture-seed";

    private const string SeedPageHtml =
        "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"icon\" href=\"data:,\">" +
        "<title>IndexedDB fixture seed</title></head><body></body></html>";

    /// <summary>
    /// Recreates the dumped database at its old version: stores (key path, auto-increment,
    /// indexes) inside <c>onupgradeneeded</c>, then every record in one read-write transaction.
    /// </summary>
    private const string SeedScript = """
        async json => {
            const fixture = JSON.parse(json);
            const request = name => new Promise((resolve, reject) => {
                name.onsuccess = () => resolve(name.result);
                name.onerror = () => reject(name.error);
            });
            await request(indexedDB.deleteDatabase(fixture.databaseName));

            const open = indexedDB.open(fixture.databaseName, fixture.version);
            open.onupgradeneeded = () => {
                const db = open.result;
                for (const store of fixture.objectStores) {
                    const options = { autoIncrement: store.autoIncrement === true };
                    if (store.keyPath !== null && store.keyPath !== undefined) {
                        options.keyPath = store.keyPath;
                    }
                    const created = db.createObjectStore(store.name, options);
                    for (const index of store.indexes ?? []) {
                        created.createIndex(index.name, index.keyPath, {
                            unique: index.unique === true,
                            multiEntry: index.multiEntry === true
                        });
                    }
                }
            };
            const db = await request(open);

            const names = fixture.objectStores.map(store => store.name);
            const tx = db.transaction(names, "readwrite");
            const done = new Promise((resolve, reject) => {
                tx.oncomplete = () => resolve();
                tx.onabort = () => reject(tx.error);
                tx.onerror = () => reject(tx.error);
            });
            let count = 0;
            for (const store of fixture.objectStores) {
                const target = tx.objectStore(store.name);
                store.records.forEach((record, i) => {
                    if (target.keyPath === null) {
                        target.put(record, store.keys[i]);
                    } else {
                        target.put(record);
                    }
                    count++;
                });
            }
            await done;
            const version = db.version;
            db.close();
            return { version, count };
        }
        """;

    /// <summary>Opens the database without a version (never upgrades it) and returns its state.</summary>
    private const string InspectScript = """
        async name => {
            const db = await new Promise((resolve, reject) => {
                const open = indexedDB.open(name);
                open.onsuccess = () => resolve(open.result);
                open.onerror = () => reject(open.error);
            });
            const state = { version: db.version, stores: Array.from(db.objectStoreNames) };
            db.close();
            return state;
        }
        """;

    /// <summary>Lists the origin's databases and dumps every record of <paramref name="name"/> as JSON.</summary>
    private const string DumpScript = """
        async name => {
            const names = (await indexedDB.databases()).map(database => database.name);
            const db = await new Promise((resolve, reject) => {
                const open = indexedDB.open(name);
                open.onsuccess = () => resolve(open.result);
                open.onerror = () => reject(open.error);
            });
            const stores = {};
            const tx = db.transaction(Array.from(db.objectStoreNames), "readonly");
            for (const store of Array.from(db.objectStoreNames)) {
                stores[store] = await new Promise((resolve, reject) => {
                    const request = tx.objectStore(store).getAll();
                    request.onsuccess = () => resolve(request.result);
                    request.onerror = () => reject(request.error);
                });
            }
            db.close();
            return { names, stores: JSON.stringify(stores) };
        }
        """;

    /// <summary>Upgrades one fixture's database to the current version through the real OS.</summary>
    /// <param name="fixtureVersion">The schema version of the dumped database.</param>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Current_os_upgrades_an_old_database_and_keeps_files_accounts_and_settings(int fixtureVersion)
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        string fixturePath = Path.Combine(solutionDirectory, "Tests", "fixtures", "indexeddb", $"hackeros-v{fixtureVersion}.json");
        string fixtureJson = await File.ReadAllTextAsync(fixturePath);
        using JsonDocument fixture = JsonDocument.Parse(fixtureJson);
        JsonElement root = fixture.RootElement;
        Assert.Equal(fixtureVersion, root.GetProperty("version").GetInt32());

        JsonElement expectations = root.GetProperty("expectations");
        string loginName = expectations.GetProperty("admin").GetProperty("loginName").GetString()!;
        string displayName = expectations.GetProperty("admin").GetProperty("displayName").GetString()!;
        string password = expectations.GetProperty("admin").GetProperty("password").GetString()!;
        string filePath = expectations.GetProperty("file").GetProperty("path").GetString()!;
        string fileContent = expectations.GetProperty("file").GetProperty("content").GetString()!;
        string settingKey = expectations.GetProperty("setting").GetProperty("key").GetString()!;
        string settingValue = expectations.GetProperty("setting").GetProperty("value").GetString()!;
        string databaseName = root.GetProperty("databaseName").GetString()!;

        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(
                playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(
                new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 1440, Height = 900 } });
            IPage page = await context.NewPageAsync();
            page.SetDefaultTimeout(30_000);
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);
            // The console only says "Failed to load resource: 404"; the response names the URL.
            page.Response += (_, response) =>
            {
                if (response.Status >= 400)
                {
                    output.WriteLine($"[http {response.Status}] {response.Request.Method} {response.Url}");
                }
            };

            // 1. The old database, before the OS ever runs on this origin. Opening the OS page
            //    to wait for the harness would create a fresh v4 database, so readiness is
            //    polled over plain HTTP instead.
            await WaitForHarnessAsync(address);
            await page.RouteAsync(address + SeedPagePath, route => route.FulfillAsync(
                new RouteFulfillOptions { Status = 200, ContentType = "text/html", Body = SeedPageHtml }));
            await page.GotoAsync(address + SeedPagePath);
            JsonElement seeded = await page.EvaluateAsync<JsonElement>(SeedScript, fixtureJson);
            Assert.Equal(fixtureVersion, seeded.GetProperty("version").GetInt32());
            output.WriteLine($"[test] recreated '{databaseName}' at v{fixtureVersion} with {seeded.GetProperty("count").GetInt32()} record(s).");

            // 2. Boot the current OS: it must offer sign-in (an account exists), not onboarding.
            await page.GotoAsync(address, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            ILocator signIn = page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" });
            ILocator onboarding = page.GetByRole(AriaRole.Heading, new() { Name = "Create administrator" });
            ILocator fatal = page.Locator(".fatal-surface, .recovery-surface");
            await signIn.Or(onboarding).Or(fatal).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });
            if (await fatal.IsVisibleAsync())
            {
                Assert.Fail($"The OS stopped on an error screen: {await fatal.InnerTextAsync()}");
            }

            Assert.False(await onboarding.IsVisibleAsync(), "The OS showed first-run onboarding: the fixture's account was lost.");

            ILocator user = page.GetByLabel("Local user");
            await Assertions.Expect(user.Locator($"option[value='{loginName}']")).ToHaveTextAsync(new Regex(Regex.Escape(displayName)));
            await user.SelectOptionAsync(loginName);
            await page.GetByLabel("Password").FillAsync(password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "App launcher" }).WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            output.WriteLine($"[test] signed in as '{loginName}' with the fixture password.");

            // 3. The database is now at the current version, with every store of that version.
            JsonElement state = await page.EvaluateAsync<JsonElement>(InspectScript, CurrentDatabaseName);
            Assert.Equal(ExpectedCurrentVersion, state.GetProperty("version").GetInt32());
            string[] stores = [.. state.GetProperty("stores").EnumerateArray().Select(store => store.GetString()!)];
            Assert.Contains("serverConnection", stores);
            Assert.Contains("syncCursors", stores);
            Assert.Contains("syncRecordState", stores);

            // 4. The file and the setting, read back through the OS itself.
            await E2ESupport.OpenAppAsync(page, "Terminal", output);
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTerminalTextAsync(rows, text => PromptPattern().IsMatch(text), "the shell prompt");

            await RunCommandAsync(page, screen, $"cat {filePath}");
            await WaitForTerminalTextAsync(rows, text => OutputFinished(text, fileContent), $"the content of {filePath}");
            output.WriteLine($"[test] {filePath} kept its content.");

            await RunCommandAsync(page, screen, "cat /etc/kazoos/appearance.config");
            Regex setting = new($@"^\s*{Regex.Escape(settingKey)}\s*=\s*{Regex.Escape(settingValue)}\s*$", RegexOptions.Multiline);
            await WaitForTerminalTextAsync(
                rows,
                text => setting.IsMatch(text) && OutputFinished(text, settingValue),
                $"{settingKey}={settingValue} in /etc/kazoos/appearance.config");
            output.WriteLine($"[test] the appearance setting {settingKey}={settingValue} survived.");

            // 5. ADR 0230: the old database is gone and no record holds an old name.
            JsonElement dump = await page.EvaluateAsync<JsonElement>(DumpScript, CurrentDatabaseName);
            string[] names = [.. dump.GetProperty("names").EnumerateArray().Select(name => name.GetString()!)];
            Assert.DoesNotContain(databaseName, names);
            Assert.DoesNotContain(names, name => OldName.IsMatch(name));
            string records = dump.GetProperty("stores").GetString()!;
            Match old = OldName.Match(records);
            Assert.False(old.Success, $"A migrated record still holds '{old.Value}': …{records[Math.Max(0, old.Index - 200)..Math.Min(records.Length, old.Index + 200)]}…");
            output.WriteLine($"[test] '{databaseName}' moved to '{CurrentDatabaseName}' without any old name.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Polls the harness over HTTP until it answers, without opening the OS page.</summary>
    private async Task WaitForHarnessAsync(string address)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
        Stopwatch elapsed = Stopwatch.StartNew();
        Exception? last = null;
        while (elapsed.Elapsed < TimeSpan.FromMinutes(3))
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(address + "/_content/Kazo.Os.Ecosystem.Host/css/app.css");
                if (response.IsSuccessStatusCode)
                {
                    output.WriteLine($"[harness] {address} answered after {elapsed.Elapsed.TotalSeconds:0.0} s.");
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                last = exception;
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException("The browser harness did not become ready.", last);
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
        while (elapsed.ElapsedMilliseconds < 30_000)
        {
            // xterm.js renders spaces as no-break spaces.
            text = (await rows.InnerTextAsync()).Replace(' ', ' ');
            if (done(text))
            {
                return text;
            }

            await Task.Delay(250);
        }

        output.WriteLine($"[test] terminal text when giving up:\n{text}");
        throw new TimeoutException($"The terminal never showed {what}.");
    }

    /// <summary>True once <paramref name="marker"/> is on screen and a shell prompt follows it.</summary>
    private static bool OutputFinished(string text, string marker)
    {
        int at = text.LastIndexOf(marker, StringComparison.Ordinal);
        return at >= 0 && PromptPattern().IsMatch(text[(at + marker.Length)..]);
    }

    /// <summary>The end of an hsh prompt: <c>$</c> or <c>#</c> at the end of a rendered line.</summary>
    [GeneratedRegex(@"[$#]\s*$", RegexOptions.Multiline)]
    private static partial Regex PromptPattern();
}
