using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

public sealed class FileExplorerHomeNavigationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Regression test for a bug where <c>FileExplorerWindow.OnAppInitializedAsync</c>
    /// overwrote the <c>_state</c> field (already correctly built with the signed-in
    /// user's real home path in <c>OnAppInitialized</c>) with a brand new
    /// <c>FileExplorerState()</c> using its hardcoded default path <c>"/home/user"</c>.
    /// That only happened to work when the login name was literally "user"; for any
    /// other login name (e.g. this suite's "admin"), File Explorer's first load
    /// enumerated a directory that was never seeded and showed "Directory not found."
    /// on open, even though the real home directory (<see cref="E2ESupport.HomePath"/>)
    /// exists and was seeded at login by <c>FileSystemSeeder</c>.
    /// </summary>
    [Fact]
    public async Task File_explorer_opens_directly_to_the_seeded_home_directory_without_a_directory_not_found_error()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Headless = true });

            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            List<string> consoleErrors = E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            await E2ESupport.OpenAppAsync(page, "File Explorer", output);

            // File Explorer must land on the real home directory on its very first
            // load, with no "Directory not found." error box shown.
            ILocator addressInput = page.GetByLabel("Current path address");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 15000 });

            ILocator errorState = page.Locator(".message-box-dialog");
            await Assertions.Expect(errorState).Not.ToBeVisibleAsync();

            // The home directory's seeded subfolders must actually be listed, proving
            // the enumeration succeeded rather than merely showing an empty/blank state.
            await Assertions.Expect(page.Locator("tr.entry-row", new() { HasText = "Desktop" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("tr.entry-row", new() { HasText = "Documents" })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("tr.entry-row", new() { HasText = "Downloads" })).ToBeVisibleAsync();

            output.WriteLine($"[test] File Explorer opened directly to '{await addressInput.InputValueAsync()}' with no error box.");
            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }
}
