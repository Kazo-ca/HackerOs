using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace HackerOs.UI.E2E.Tests;

/// <summary>
/// ADR 0151 and ADR 0159, end to end: the entries apps register (the explorer's "Extract here", the
/// Web Server Manager's "Publish as a web site…") reach the file menus through
/// <c>/etc/hackeros/context-menus.d</c>, and a click launches the owner with the paths. ADR 0218:
/// Settings' « Set as wallpaper » on an image, with one screen.
/// </summary>
public sealed class FileMenuEntriesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Extract_here_is_registered_after_open_and_extracts_the_archive()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        string folderName = $"e2e-archive-{Guid.NewGuid():N}"[..20];
        string zipPath = Path.Combine(Path.GetTempPath(), folderName + ".zip");
        using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            await using StreamWriter writer = new(archive.CreateEntry("hello.txt").Open());
            await writer.WriteAsync("hello from the archive\n");
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

            // Upload into the home folder once its listing is shown.
            ILocator addressInput = page.GetByLabel("Current path address");
            await Assertions.Expect(addressInput).ToHaveValueAsync(E2ESupport.HomePath, new() { Timeout = 15000 });
            await page.Locator("tr.entry-row", new() { HasText = "Documents" }).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await page.Locator("#fileInput").SetInputFilesAsync(zipPath);

            ILocator zipRow = page.Locator("tr.entry-row", new() { HasText = folderName + ".zip" });
            await zipRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            await zipRow.ClickAsync(new() { Button = MouseButton.Right });

            ILocator items = page.Locator(".hack-context-menu-panel [role=menuitem] .hack-context-menu-title");
            await Assertions.Expect(items.Filter(new() { HasText = "Extract here" })).ToBeVisibleAsync(new() { Timeout = 10000 });
            IReadOnlyList<string> titles = await items.AllInnerTextsAsync();
            output.WriteLine($"[test] archive menu: {string.Join(" | ", titles)}");
            int open = titles.ToList().IndexOf("Open");
            Assert.True(open >= 0, "the archive menu has Open");
            Assert.Equal("Extract here", titles[open + 1]);
            // A file is not a folder: the Web Server Manager's entry (directories only) stays out.
            Assert.DoesNotContain(titles, static title => title.StartsWith("Publish as a web site", StringComparison.Ordinal));

            await items.Filter(new() { HasText = "Extract here" }).ClickAsync();

            // A second explorer window is launched with the intent, on the archive's folder, and
            // extracts next to it; it opens on top, so its row is the last one.
            ILocator folderRow = page.Locator("tr.entry-row", new() { HasText = folderName }).Filter(new() { HasNotText = ".zip" }).Last;
            await folderRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
            output.WriteLine($"[test] '{folderName}' was extracted.");

            await folderRow.ClickAsync(new() { Button = MouseButton.Right });
            await Assertions.Expect(items.Filter(new() { HasText = "Publish as a web site" })).ToBeVisibleAsync(new() { Timeout = 10000 });
            Assert.DoesNotContain("Extract here", await items.AllInnerTextsAsync());
            await page.Keyboard.PressAsync("Escape");

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task Set_as_wallpaper_follows_open_with_and_changes_the_desktop_without_a_window()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        // A real WebP image: one of the wallpapers shipped with the OS.
        string imageName = $"e2e-wall-{Guid.NewGuid():N}"[..17] + ".webp";
        string imagePath = Path.Combine(Path.GetTempPath(), imageName);
        File.Copy(Path.Combine(solutionDirectory, "Platform", "HackerOs.Theming.Blazor", "wwwroot", "wallpapers", "aurora.webp"), imagePath);

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
            await page.Locator("#fileInput").SetInputFilesAsync(imagePath);

            ILocator imageRow = page.Locator("tr.entry-row", new() { HasText = imageName });
            await imageRow.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
            int windows = await page.Locator("header.window-chrome").CountAsync();
            await imageRow.ClickAsync(new() { Button = MouseButton.Right });

            ILocator items = page.Locator(".hack-context-menu-panel [role=menuitem] .hack-context-menu-title");
            await Assertions.Expect(items.Filter(new() { HasText = "Set as wallpaper" })).ToBeVisibleAsync(new() { Timeout = 10000 });
            IReadOnlyList<string> titles = await items.AllInnerTextsAsync();
            output.WriteLine($"[test] image menu: {string.Join(" | ", titles)}");
            int openWith = titles.ToList().FindIndex(static title => title.StartsWith("Open with", StringComparison.Ordinal));
            Assert.True(openWith >= 0, "the image menu has Open with");
            Assert.Equal("Set as wallpaper", titles[openWith + 1]);
            // One screen: a plain entry, no submenu of screens.
            Assert.DoesNotContain("Screen 1 and the others", titles);

            await items.Filter(new() { HasText = "Set as wallpaper" }).ClickAsync();

            ILocator wallpaper = page.Locator($"[data-wallpaper$='{imageName}']");
            await wallpaper.First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 30000 });
            output.WriteLine($"[test] wallpaper shown: {await wallpaper.First.GetAttributeAsync("data-wallpaper")}");
            await Assertions.Expect(page.GetByText("Wallpaper changed").First).ToBeVisibleAsync(new() { Timeout = 15000 });
            Assert.Equal(windows, await page.Locator("header.window-chrome").CountAsync());

            Assert.Empty(consoleErrors);
        }
        finally
        {
            E2ESupport.StopProcess(server);
            File.Delete(imagePath);
        }
    }
}
