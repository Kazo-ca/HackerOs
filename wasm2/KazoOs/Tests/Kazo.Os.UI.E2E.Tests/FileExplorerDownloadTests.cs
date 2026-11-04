using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Kazo.Os.Platform.Core.FileSystem;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

public sealed class FileExplorerDownloadTests(ITestOutputHelper output)
{
    /// <summary>
    /// Seeds a known fixture tree via the gated Test Demo control, downloads it as a
    /// zip through File Explorer, and asserts the archive contains exactly the seeded
    /// entries with exactly the seeded content — not just "some zip came out".
    /// </summary>
    [Fact]
    public async Task File_explorer_downloads_seeded_folder_as_zip_with_expected_structure_and_content()
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
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);
            await E2ESupport.InstallBlobCaptureAsync(page);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            string fixtureRoot = await E2ESupport.SeedFixturesAsync(page, output);
            Assert.Equal($"{E2ESupport.HomePath}/Documents/{TestDemoFixtureSeeder.FixtureRootName}", fixtureRoot);

            await E2ESupport.OpenAppAsync(page, "File Explorer", output);

            ILocator downloadBtn = page.GetByRole(AriaRole.Button, new() { Name = "Download" });
            await downloadBtn.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // Navigate straight to the fixture's parent directory via the address bar,
            // rather than clicking through folders, so the test stays stable if the
            // desktop's default starting folder ever changes.
            ILocator addressInput = page.GetByLabel("Current path address");
            await addressInput.FillAsync($"{E2ESupport.HomePath}/Documents");
            await addressInput.PressAsync("Enter");

            await E2ESupport.SelectFileExplorerEntryAsync(page, TestDemoFixtureSeeder.FixtureRootName, output);

            (string fileName, byte[] zipBytes) = await E2ESupport.CaptureNextDownloadAsync(
                page, () => downloadBtn.ClickAsync(), output);

            Assert.Equal($"{TestDemoFixtureSeeder.FixtureRootName}.zip", fileName);

            Dictionary<string, string> expectedEntries = new()
            {
                [$"{TestDemoFixtureSeeder.FixtureRootName}/{TestDemoFixtureSeeder.AlphaFileName}"] = TestDemoFixtureSeeder.AlphaContent,
                [$"{TestDemoFixtureSeeder.FixtureRootName}/{TestDemoFixtureSeeder.EditableFileName}"] = TestDemoFixtureSeeder.EditableOriginalContent,
                [$"{TestDemoFixtureSeeder.FixtureRootName}/{TestDemoFixtureSeeder.NestedDirectoryName}/{TestDemoFixtureSeeder.NestedFileName}"] = TestDemoFixtureSeeder.NestedContent,
            };

            using (MemoryStream zipStream = new(zipBytes))
            using (ZipArchive archive = new(zipStream, ZipArchiveMode.Read))
            {
                output.WriteLine($"[test] zip entries: {string.Join(", ", archive.Entries.Select(e => e.FullName))}");

                Assert.Equal(
                    expectedEntries.Keys.OrderBy(k => k, StringComparer.Ordinal),
                    archive.Entries.Select(e => e.FullName).OrderBy(k => k, StringComparer.Ordinal));

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    using StreamReader reader = new(entry.Open(), Encoding.UTF8);
                    string actualContent = await reader.ReadToEndAsync();
                    Assert.Equal(expectedEntries[entry.FullName], actualContent);
                }
            }

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
