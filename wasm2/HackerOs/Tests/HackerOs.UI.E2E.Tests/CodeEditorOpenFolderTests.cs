using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

public sealed class CodeEditorOpenFolderTests(ITestOutputHelper output)
{
    private const string ProjectName = "my-project";

    /// <summary>
    /// ADR 0175: Code Editor's "Open Folder" dialog creates a folder from its toolbar, on the desktop
    /// and on the phone: the OS file control's own "New folder" names it and puts it in rename, the
    /// user types its name, then opens it as the workspace.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_folder_dialog_creates_a_new_folder_and_opens_it(bool mobile)
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
            if (mobile)
            {
                await OpenOnPhoneAsync(page);
            }
            else
            {
                await E2ESupport.OpenAppAsync(page, "Code Editor", output);
            }

            await page.Locator("#code-open-folder").ClickAsync();
            ILocator dialog = page.Locator("section.file-dialog", new() { HasText = "Open Folder" });
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });

            // The OS file control is shown, so the dialog's own name box is not: one way to create.
            await dialog.Locator(".file-dialog-toolbar").WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            Assert.Equal(0, await dialog.Locator(".create-control").CountAsync());

            await dialog.GetByRole(AriaRole.Button, new() { Name = "New folder", Exact = true }).ClickAsync();
            ILocator rename = dialog.Locator("input.file-view-rename-input");
            await rename.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await rename.FillAsync(ProjectName);
            await page.ScreenshotAsync(new() { Path = Path.Combine(solutionDirectory, "Tests", "TestResults", "open-folder", $"{(mobile ? "mobile" : "desktop")}.png") });
            await rename.PressAsync("Enter");
            await rename.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15000 });
            output.WriteLine("[test] New folder created and renamed in the dialog.");

            // The new folder is selected after its rename; click it only if it is not.
            ILocator folder = dialog.Locator("tr.entry-row", new() { HasText = ProjectName });
            await folder.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15000 });
            output.WriteLine($"[test] row selected after rename: {await folder.GetAttributeAsync("aria-selected")}");
            if (await folder.GetAttributeAsync("aria-selected") != "true")
            {
                await folder.ClickAsync();
            }

            await Assertions.Expect(dialog.Locator("footer span")).ToHaveTextAsync($"{E2ESupport.HomePath}/{ProjectName}");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Select", Exact = true }).ClickAsync();
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden, Timeout = 15000 });
            output.WriteLine("[test] The new folder was opened as the workspace.");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
        }
    }

    /// <summary>Switches to the mobile platform at phone size and launches Code Editor from the app drawer.</summary>
    private static async Task OpenOnPhoneAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "System clock, notifications, and calendar" }).ClickAsync();
        await page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" })
            .GetByRole(AriaRole.Radio, new() { Name = "Mobile", Exact = true }).ClickAsync();
        await page.SetViewportSizeAsync(390, 844);
        ILocator home = page.Locator(".mobile-home-screen");
        await Assertions.Expect(home).ToBeVisibleAsync();

        await home.GetByRole(AriaRole.Button, new() { Name = "All applications", Exact = true }).ClickAsync();
        ILocator drawer = page.GetByRole(AriaRole.Dialog, new() { Name = "All applications", Exact = true });
        await drawer.GetByRole(AriaRole.Searchbox, new() { Name = "Search applications" }).FillAsync("Code Editor");
        await drawer.GetByRole(AriaRole.Button, new() { Name = "Code Editor", Exact = true }).ClickAsync();
    }
}
