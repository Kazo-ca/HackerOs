using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// The site data of chantier <c>web/12</c> (ADR 0088) in a real Chromium, with the Intranet sample: a note
/// saved through an MVC form lands in the host browser's IndexedDB database
/// <c>hackweb.org.hackeros.samples.intranet</c>; the browser offers the typed title again on the next form
/// (form history, an OS file); the site's debug log is a file under <c>/var/log/hackweb/</c> the terminal
/// can read; the Diagnostic app lists the database and clears it.
/// </summary>
public sealed class BrowserSiteDataTests(ITestOutputHelper output)
{
    private const string Database = "hackweb.org.hackeros.samples.intranet";

    /// <summary>Counts the records of a store by opening the database from the page, as DevTools would.</summary>
    private const string CountNotes = """
        async name => await new Promise((resolve, reject) => {
            const request = indexedDB.open(name);
            request.onerror = () => reject(request.error);
            request.onsuccess = () => {
                const db = request.result;
                const count = db.transaction('notes', 'readonly').objectStore('notes').count();
                count.onsuccess = () => { resolve(count.result); db.close(); };
                count.onerror = () => { reject(count.error); db.close(); };
            };
        })
        """;

    private const string DatabaseNames = "async () => (await indexedDB.databases()).map(database => database.name)";

    [Fact]
    public async Task A_site_keeps_its_notes_in_IndexedDB_logs_to_var_log_and_the_browser_remembers_the_form()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: true, output);

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
            await E2ESupport.OpenAppAsync(page, "Browser", output);

            ILocator addressInput = page.Locator("#browser-address-input");
            await addressInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            IFrameLocator frame = page.FrameLocator(".page-frame");

            // --- A note saved by a form goes to the site's IndexedDB database, with the three welcome notes ---
            await addressInput.FillAsync("http://intranet.hack/Notes/Create");
            await addressInput.PressAsync("Enter");
            await frame.Locator("#Title").FillAsync("Quarterly audit", new() { Timeout = 30000 });
            await frame.Locator("#Body").FillAsync("Check the firewall rules.");
            await frame.Locator("#note-save").ClickAsync();
            await Assertions.Expect(frame.Locator("#note-details")).ToContainTextAsync("Quarterly audit", new() { Timeout = 30000 });
            Assert.Contains(Database, await page.EvaluateAsync<string[]>(DatabaseNames));
            Assert.Equal(4, await page.EvaluateAsync<int>(CountNotes, Database));
            output.WriteLine("[test] the note is in the site's IndexedDB database.");

            // --- The browser offers the submitted title again in the next form (datalist) ---
            await addressInput.FillAsync("http://intranet.hack/Notes/Create");
            await addressInput.PressAsync("Enter");
            ILocator title = frame.Locator("#Title");
            await title.ClickAsync(new() { Timeout = 30000 });
            await Assertions.Expect(frame.Locator("datalist option[value='Quarterly audit']")).ToHaveCountAsync(1, new() { Timeout = 20000 });
            Assert.StartsWith("hackos-form-history-", await title.GetAttributeAsync("list"), StringComparison.Ordinal);
            output.WriteLine("[test] the form history offered the title back.");

            // --- The debug log is an OS file the terminal can read ---
            await E2ESupport.OpenAppAsync(page, "Terminal", output);
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTextAsync(rows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
            await screen.ClickAsync();
            await page.Keyboard.TypeAsync("grep created /var/log/hackweb/org.hackeros.samples.intranet.log");
            await page.Keyboard.PressAsync("Enter");
            string log = await WaitForTextAsync(rows, text => text.Contains("Note 4 created by", StringComparison.Ordinal), "the site's debug log line");
            Assert.Contains("info: Kazo.Os.Samples.Intranet.Controllers.NotesController[0]", log, StringComparison.Ordinal);
            output.WriteLine("[test] the debug log holds the controller's ILogger line.");

            // --- The Diagnostic app lists the database and clears it ---
            await E2ESupport.OpenAppAsync(page, "IndexedDB Diagnostic", output);
            ILocator site = page.Locator(".diag-site-item", new() { HasText = "org.hackeros.samples.intranet" });
            // Not a system app: system.storage.inspect needs an administrator's approval (ADR 0050).
            await page.GetByLabel("Administrator name", new() { Exact = true }).FillAsync(E2ESupport.LoginName, new() { Timeout = 20000 });
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(E2ESupport.Password);
            await page.GetByRole(AriaRole.Button, new() { Name = "Allow", Exact = true }).ClickAsync();
            try
            {
                await site.ClickAsync(new() { Timeout = 20000 });
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                output.WriteLine("[page] " + await page.Locator("body").InnerTextAsync());
                throw;
            }
            await Assertions.Expect(page.Locator(".diag-store-name", new() { HasText = Database })).ToBeVisibleAsync(new() { Timeout = 20000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "🗑 Clear this database" }).ClickAsync();
            await page.Locator(".diag-site-actions .diag-btn-danger", new() { HasText = "Delete" }).ClickAsync();
            await Assertions.Expect(page.Locator(".diag-sidebar-empty")).ToBeVisibleAsync(new() { Timeout = 20000 });
            Assert.DoesNotContain(Database, await page.EvaluateAsync<string[]>(DatabaseNames));
            output.WriteLine("[test] the Diagnostic app cleared the site's database.");
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Polls the terminal's rendered rows until <paramref name="done"/> holds.</summary>
    private async Task<string> WaitForTextAsync(ILocator rows, Func<string, bool> done, string what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string text = string.Empty;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            // Long lines wrap at the terminal's width: the rows are read as one text.
            text = (await rows.InnerTextAsync()).Replace('\u00a0', ' ').Replace("\n", string.Empty, StringComparison.Ordinal);
            if (done(text))
            {
                return text;
            }

            await Task.Delay(200);
        }

        output.WriteLine(text);
        throw new TimeoutException($"The terminal never showed {what}.");
    }
}
