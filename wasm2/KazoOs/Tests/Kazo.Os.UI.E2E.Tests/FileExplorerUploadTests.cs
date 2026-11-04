using System.Diagnostics;
using Kazo.Os.Platform.Core.FileSystem;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

public sealed class FileExplorerUploadTests(ITestOutputHelper output)
{
    /// <summary>
    /// Regression test: uploading into a folder that already has content used to
    /// silently do nothing (FileExplorerWindow.UploadFilesAsync hard-coded the new
    /// file's parent-revision precondition to 1, which only matched a brand-new empty
    /// directory; any folder that already had entries — like the seeded fixtures — has
    /// a higher revision, so CreateAsync always failed with a swallowed
    /// RevisionConflict). Uploads now go through the file system's unconditional
    /// create/write mode instead of guessing a revision.
    /// </summary>
    [Fact]
    public async Task File_explorer_upload_adds_file_to_a_non_empty_folder()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: true, output);

        string uploadFilePath = Path.Combine(Path.GetTempPath(), $"e2e-upload-{Guid.NewGuid():N}.txt");
        const string uploadContent = "hello from the upload regression test\n";
        await File.WriteAllTextAsync(uploadFilePath, uploadContent);

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

            // Seeds /home/admin/Documents/e2e-fixtures with 3 existing entries, so its
            // revision is well past the hard-coded "1" the bug assumed.
            await E2ESupport.SeedFixturesAsync(page, output);

            await E2ESupport.OpenAppAsync(page, "File Explorer", output);

            ILocator addressInput = page.GetByLabel("Current path address");
            await addressInput.FillAsync($"{E2ESupport.HomePath}/Documents/{TestDemoFixtureSeeder.FixtureRootName}");
            await addressInput.PressAsync("Enter");

            ILocator existingEntry = page.Locator("tr.entry-row", new PageLocatorOptions { HasText = TestDemoFixtureSeeder.AlphaFileName });
            await existingEntry.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            output.WriteLine("[test] confirmed target folder already has existing entries (non-1 revision).");

            await page.Locator("#fileInput").SetInputFilesAsync(uploadFilePath);

            string uploadedName = Path.GetFileName(uploadFilePath);
            ILocator uploadedRow = page.Locator("tr.entry-row", new PageLocatorOptions { HasText = uploadedName });
            await uploadedRow.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            output.WriteLine($"[test] '{uploadedName}' appeared in the listing after upload.");

            // Refresh independently of the upload's own auto-refresh, matching the
            // user's report ("even after a refresh") — re-navigate to the same path.
            await addressInput.FillAsync($"{E2ESupport.HomePath}/Documents/{TestDemoFixtureSeeder.FixtureRootName}");
            await addressInput.PressAsync("Enter");
            await uploadedRow.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

            await E2ESupport.SelectFileExplorerEntryAsync(page, uploadedName, output);
            ILocator downloadBtn = page.GetByRole(AriaRole.Button, new() { Name = "Download" });
            (string fileName, byte[] bytes) = await E2ESupport.CaptureNextDownloadAsync(
                page, () => downloadBtn.ClickAsync(), output);

            Assert.Equal(uploadedName, fileName);
            Assert.Equal(uploadContent, System.Text.Encoding.UTF8.GetString(bytes));
            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
            File.Delete(uploadFilePath);
        }
    }
}
