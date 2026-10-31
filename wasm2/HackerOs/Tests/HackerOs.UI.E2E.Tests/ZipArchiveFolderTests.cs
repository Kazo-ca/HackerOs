using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0185 §6-9, end to end: a double click on a <c>.zip</c> in the File Explorer opens it as a
/// read-only folder, its folders can be entered, and nothing can be changed inside it.
/// </summary>
public sealed class ZipArchiveFolderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Double_click_opens_a_zip_as_a_read_only_folder()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        string archiveName = $"e2e-browse-{Guid.NewGuid():N}"[..19] + ".zip";
        string zipPath = Path.Combine(Path.GetTempPath(), archiveName);
        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            await using (StreamWriter writer = new(archive.CreateEntry("readme.txt").Open()))
            {
                await writer.WriteAsync("top\n");
            }

            await using (StreamWriter writer = new(archive.CreateEntry("docs/hello.txt").Open()))
            {
                await writer.WriteAsync("hello from the archive\n");
            }
        }

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);
            await E2ESupport.OpenAppAsync(page, "File Explorer", output);

            ILocator addressInput = page.GetByLabel("Current path address");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 15000 });
            await page.Locator("tr.entry-row", new() { HasText = "Documents" }).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await page.Locator("#fileInput").SetInputFilesAsync(zipPath);

            ILocator zipRow = page.Locator("tr.entry-row", new() { HasText = archiveName });
            await zipRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await zipRow.DblClickAsync();

            // Inside the archive: its entries, the folders it only implies, and its name in the path.
            await Assertions.Expect(addressInput).ToHaveValueAsync($"{E2ESupport.HomePath}/{archiveName}", new() { Timeout = 15000 });
            await page.Locator("tr.entry-row", new() { HasText = "readme.txt" }).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            ILocator docsRow = page.Locator("tr.entry-row", new() { HasText = "docs" });
            await docsRow.DblClickAsync();
            await Assertions.Expect(addressInput).ToHaveValueAsync($"{E2ESupport.HomePath}/{archiveName}/docs", new() { Timeout = 15000 });
            ILocator helloRow = page.Locator("tr.entry-row", new() { HasText = "hello.txt" });
            await helloRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            output.WriteLine("[test] the archive was entered as a folder.");

            // Read-only: the menu of an entry has no Rename, Delete or Cut.
            await helloRow.ClickAsync(new() { Button = MouseButton.Right });
            ILocator items = page.Locator(".hack-context-menu-panel [role=menuitem] .hack-context-menu-title");
            await Assertions.Expect(items.Filter(new() { HasText = "Open" }).First).ToBeVisibleAsync(new() { Timeout = 10000 });
            IReadOnlyList<string> titles = await items.AllInnerTextsAsync();
            output.WriteLine($"[test] entry menu: {string.Join(" | ", titles)}");
            Assert.DoesNotContain("Rename", titles);
            Assert.DoesNotContain("Delete", titles);
            Assert.DoesNotContain("Cut", titles);
            Assert.DoesNotContain("Compress to .zip…", titles);
            await page.Keyboard.PressAsync("Escape");

            // A new folder inside the archive is refused with a clear message.
            await page.Locator(".actions-toolbar .action-btn", new() { HasText = "New Folder" }).ClickAsync();
            await Assertions.Expect(page.GetByText("This archive is read-only", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = 10000 });

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
            File.Delete(zipPath);
        }
    }
}
