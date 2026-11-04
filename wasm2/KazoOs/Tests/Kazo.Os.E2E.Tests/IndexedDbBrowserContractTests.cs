using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Kazo.Os.E2E.Tests;

/// <summary>Runs browser persistence contracts against native Chromium IndexedDB.</summary>
public sealed partial class IndexedDbBrowserContractTests
{
    /// <summary>Verifies save overwrite, folder creation, and filesystem denial dialog branches.</summary>
    [Fact]
    public async Task File_dialog_save_folder_and_denial_flows_render_in_real_browser()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            page.Console += (_, message) => Console.WriteLine($"browser console [{message.Type}]: {message.Text}");
            page.PageError += (_, error) => Console.WriteLine($"browser page error: {error}");
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=dialog");
            page.RequestFailed += (_, request) => failures.Add($"network: {request.Method} {request.Url}");

            await page.GetByRole(AriaRole.Button, new() { Name = "Save existing file" }).ClickAsync();
            // Not page.GetByRole(Dialog, Name:"Save report"): the owning window (an
            // <article role="dialog">) can share the same accessible name as the file
            // dialog it contains, now that windows correctly expose one (WindowHost.razor's
            // aria-labelledby fix) — scope by the dialog's own element instead.
            ILocator saveDialog = page.Locator("section.file-dialog", new() { HasText = "Save report" });
            await saveDialog.WaitForAsync();
            await saveDialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            ILocator overwrite = saveDialog.GetByRole(AriaRole.Alertdialog, new() { Name = "Confirm overwrite" });
            await overwrite.WaitForAsync();
            Assert.Contains("/home/user/readme.txt", await overwrite.InnerTextAsync(), StringComparison.Ordinal);
            await overwrite.GetByRole(AriaRole.Button, new() { Name = "Replace" }).ClickAsync();
            await page.GetByText("Saved:/home/user/readme.txt", new() { Exact = true }).WaitForAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Select or create folder" }).ClickAsync();
            ILocator folderDialog = page.Locator("section.file-dialog", new() { HasText = "Choose workspace" });
            await folderDialog.WaitForAsync();
            await folderDialog.GetByRole(AriaRole.Textbox, new() { Name = "New folder name" }).FillAsync("Projects");
            await folderDialog.GetByRole(AriaRole.Button, new() { Name = "New folder" }).ClickAsync();
            ILocator project = folderDialog.GetByRole(AriaRole.Option, new() { Name = "Projects Directory" });
            await project.WaitForAsync();
            await project.ClickAsync();
            Assert.Equal("true", (await project.GetAttributeAsync("aria-selected"))?.ToLowerInvariant());
            await folderDialog.GetByRole(AriaRole.Button, new() { Name = "Select", Exact = true }).ClickAsync();
            await page.GetByText("Folder:/home/user/Projects", new() { Exact = true }).WaitForAsync();

            await page.GetByRole(AriaRole.Button, new() { Name = "Open denied folder" }).ClickAsync();
            ILocator deniedDialog = page.Locator("section.file-dialog", new() { HasText = "Denied folder" });
            await deniedDialog.GetByRole(AriaRole.Alert).WaitForAsync();
            Assert.Contains("PermissionDenied", await deniedDialog.GetByRole(AriaRole.Alert).InnerTextAsync(), StringComparison.Ordinal);
            await deniedDialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
            await page.GetByText("DeniedCancelled", new() { Exact = true }).WaitForAsync();
            Assert.Empty(failures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies the rendered file-open dialog, filtering, multi-select, modality, and cancellation.</summary>
    [Fact]
    public async Task File_dialog_open_integrates_filters_selection_modality_and_escape_cancellation()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=dialog");
            page.RequestFailed += (_, request) => failures.Add($"network: {request.Method} {request.Url}");
            ILocator owner = page.Locator("[data-app-id='org.kazoos.browser.dialog-owner']").Locator("xpath=ancestor::article");
            await owner.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            ILocator trigger = page.GetByRole(AriaRole.Button, new() { Name = "Open filtered files" });

            await trigger.ClickAsync();
            ILocator dialog = page.Locator("section.file-dialog", new() { HasText = "Choose text files" });
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            ILocator modalWindow = dialog.Locator("xpath=ancestor::article");
            Assert.Equal("true", await modalWindow.GetAttributeAsync("aria-modal"));
            Assert.Equal("true", await owner.GetAttributeAsync("aria-hidden"));
            Assert.NotNull(await owner.GetAttributeAsync("inert"));
            await dialog.FocusAsync();
            await page.Keyboard.PressAsync("Escape");
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            await page.GetByText("Cancelled", new() { Exact = true }).WaitForAsync();
            Assert.Equal("false", await owner.GetAttributeAsync("aria-hidden"));
            Assert.Contains("is-focused", await owner.GetAttributeAsync("class"), StringComparison.Ordinal);

            await trigger.ClickAsync();
            dialog = page.Locator("section.file-dialog", new() { HasText = "Choose text files" });
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            ILocator options = dialog.GetByRole(AriaRole.Option);
            Assert.Equal(3, await options.CountAsync());
            Assert.Equal(".private.txtFile", (await options.Nth(0).InnerTextAsync()).Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal));
            Assert.Equal("notes.mdFile", (await options.Nth(1).InnerTextAsync()).Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal));
            Assert.Equal("readme.txtFile", (await options.Nth(2).InnerTextAsync()).Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal));
            await options.Nth(1).ClickAsync();
            Assert.Equal("true", (await options.Nth(1).GetAttributeAsync("aria-selected"))?.ToLowerInvariant());
            Assert.Equal("/home/user/notes.md", await dialog.Locator(".selection-summary").InnerTextAsync());
            await options.Nth(2).ClickAsync(new LocatorClickOptions { Modifiers = [KeyboardModifier.Control] });
            await page.WaitForFunctionAsync("() => [...document.querySelectorAll('[role=option]')].filter(element => element.getAttribute('aria-selected')?.toLowerCase() === 'true').length === 2");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true }).ClickAsync();
            await dialog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            await page.GetByText("Selected:/home/user/notes.md,/home/user/readme.txt", new() { Exact = true }).WaitForAsync();
            Assert.Empty(failures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies keyboard controls, taskbar restore, modality, close, and viewport constraints.</summary>
    [Fact]
    public async Task Window_runtime_handles_keyboard_modality_close_and_viewport_changes()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=window");
            ILocator primary = page.Locator("[data-app-id='org.kazoos.browser.primary']").Locator("xpath=ancestor::article");
            await primary.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            ILocator maximize = primary.GetByLabel("Maximize");
            await maximize.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => document.querySelector('[data-app-id=\"org.kazoos.browser.primary\"]')?.closest('article')?.dataset.windowWidth === '960'");
            ILocator restore = primary.GetByLabel("Restore");
            await restore.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            Assert.Equal("360", await primary.GetAttributeAsync("data-window-width"));

            ILocator minimize = primary.GetByLabel("Minimize");
            await minimize.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await primary.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            ILocator taskbarRestore = page.GetByRole(AriaRole.Button, new() { Name = "Restore Primary" });
            await taskbarRestore.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await primary.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            ILocator openModal = page.GetByRole(AriaRole.Button, new() { Name = "Open Primary Modal" });
            await openModal.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            ILocator modal = page.Locator("[data-app-id='org.kazoos.browser.modal']").Locator("xpath=ancestor::article");
            await modal.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal("true", await modal.GetAttributeAsync("aria-modal"));
            Assert.Equal("true", await primary.GetAttributeAsync("aria-hidden"));
            Assert.NotNull(await primary.GetAttributeAsync("inert"));

            ILocator closeModal = modal.GetByLabel("Close");
            await closeModal.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await modal.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            Assert.Equal("false", await primary.GetAttributeAsync("aria-hidden"));
            Assert.Contains("is-focused", await primary.GetAttributeAsync("class"), StringComparison.Ordinal);

            ILocator mobileViewport = page.GetByRole(AriaRole.Button, new() { Name = "Use Mobile Viewport" });
            await mobileViewport.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await page.WaitForFunctionAsync("() => Number(document.querySelector('[data-app-id=\"org.kazoos.browser.primary\"]')?.closest('article')?.dataset.windowWidth) <= 480");
            Assert.True(double.Parse((await primary.GetAttributeAsync("data-window-width"))!, System.Globalization.CultureInfo.InvariantCulture) <= 480);
            Assert.True(double.Parse((await primary.GetAttributeAsync("data-window-height"))!, System.Globalization.CultureInfo.InvariantCulture) <= 320);

            ILocator closePrimary = primary.GetByLabel("Close");
            await closePrimary.FocusAsync();
            await page.Keyboard.PressAsync("Enter");
            await primary.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies every rendered resize handle reaches the authoritative C# geometry path.</summary>
    [Fact]
    public async Task Window_runtime_handles_every_resize_edge_in_real_browser()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);
        (string Edge, string X, string Y, string Width, string Height)[] cases =
        [
            ("top", "80", "85", "360", "265"),
            ("right", "80", "70", "380", "280"),
            ("bottom", "80", "70", "360", "295"),
            ("left", "100", "70", "340", "280"),
            ("top-left", "100", "85", "340", "265"),
            ("top-right", "80", "85", "380", "265"),
            ("bottom-right", "80", "70", "380", "295"),
            ("bottom-left", "100", "70", "340", "295"),
        ];

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            page.Console += (_, message) => Console.WriteLine($"browser console [{message.Type}]: {message.Text}");
            page.PageError += (_, error) => Console.WriteLine($"browser page error: {error}");

            foreach ((string edge, string x, string y, string width, string height) in cases)
            {
                await NavigateWhenReadyAsync(page, $"{address}/?scenario=window");
                ILocator primary = page.Locator("[data-app-id='org.kazoos.browser.primary']").Locator("xpath=ancestor::article");
                await primary.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                await primary.Locator($"[data-resize-edge='{edge}']").EvaluateAsync(
                    """
                    element => {
                        element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerId: 51, pointerType: 'pen', clientX: 400, clientY: 200, button: 0 }));
                        element.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, pointerId: 51, pointerType: 'pen', clientX: 420, clientY: 215, buttons: 1 }));
                        element.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, pointerId: 51, pointerType: 'pen', clientX: 420, clientY: 215, button: 0 }));
                    }
                    """);
                await page.WaitForFunctionAsync(
                    "expected => { const data = document.querySelector('[data-app-id=\"org.kazoos.browser.primary\"]')?.closest('article')?.dataset; return data?.windowX === expected.x && data?.windowY === expected.y && data?.windowWidth === expected.width && data?.windowHeight === expected.height; }",
                    new { x, y, width, height });

                Assert.Equal(x, await primary.GetAttributeAsync("data-window-x"));
                Assert.Equal(y, await primary.GetAttributeAsync("data-window-y"));
                Assert.Equal(width, await primary.GetAttributeAsync("data-window-width"));
                Assert.Equal(height, await primary.GetAttributeAsync("data-window-height"));
            }
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies rendered Window geometry and Pointer Events in real Chrome.</summary>
    [Fact]
    public async Task Window_runtime_renders_and_handles_mouse_and_touch_pointer_gestures()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync(
                new BrowserNewContextOptions { HasTouch = true, ViewportSize = new() { Width = 1100, Height = 760 } });
            IPage page = await context.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                Console.WriteLine($"browser console [{message.Type}]: {message.Text}");
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            page.PageError += (_, error) => failures.Add($"page: {error}");
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=window");
            page.RequestFailed += (_, request) => failures.Add($"network: {request.Method} {request.Url}");
            ILocator scenario = page.Locator(".window-browser-scenario");
            await scenario.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            ILocator primary = page.Locator("[data-app-id='org.kazoos.browser.primary']").Locator("xpath=ancestor::article");
            ILocator secondary = page.Locator("[data-app-id='org.kazoos.browser.secondary']").Locator("xpath=ancestor::article");
            await primary.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            ILocator title = primary.Locator("[data-window-gesture='move']");
            var titleBox = (await title.BoundingBoxAsync())!;
            await page.Mouse.MoveAsync(titleBox.X + 30, titleBox.Y + 20);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(titleBox.X + 90, titleBox.Y + 60, new MouseMoveOptions { Steps = 4 });
            await page.Mouse.UpAsync();
            await page.WaitForFunctionAsync("() => Number(document.querySelector('[data-app-id=\"org.kazoos.browser.primary\"]')?.closest('article')?.dataset.windowX) > 80");

            float renderedX = float.Parse((await primary.GetAttributeAsync("data-window-x"))!, System.Globalization.CultureInfo.InvariantCulture);
            float renderedY = float.Parse((await primary.GetAttributeAsync("data-window-y"))!, System.Globalization.CultureInfo.InvariantCulture);
            var movedBox = (await primary.BoundingBoxAsync())!;
            Assert.InRange(movedBox.X, renderedX - 0.5F, renderedX + 0.5F);
            Assert.InRange(movedBox.Y, renderedY - 0.5F, renderedY + 0.5F);
            int primaryZ = int.Parse((await primary.GetAttributeAsync("data-window-z"))!);
            int secondaryZ = int.Parse((await secondary.GetAttributeAsync("data-window-z"))!);
            Assert.True(primaryZ > secondaryZ, $"Primary z={primaryZ}; secondary z={secondaryZ}.");

            await primary.Locator("[data-resize-edge='right']").EvaluateAsync(
                """
                element => {
                    element.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerId: 41, pointerType: 'touch', clientX: 499, clientY: 190, button: 0 }));
                    element.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, pointerId: 41, pointerType: 'touch', clientX: 549, clientY: 190, buttons: 1 }));
                    element.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, pointerId: 41, pointerType: 'touch', clientX: 549, clientY: 190, button: 0 }));
                }
                """);
            await page.WaitForFunctionAsync("() => document.querySelector('[data-app-id=\"org.kazoos.browser.primary\"]')?.closest('article')?.dataset.windowWidth === '410'");
            Assert.Equal("410", await primary.GetAttributeAsync("data-window-width"));
            Assert.InRange((await primary.BoundingBoxAsync())!.Width, 409.5, 410.5);

            await primary.GetByLabel("Maximize").ClickAsync();
            Assert.Equal("960", await primary.GetAttributeAsync("data-window-width"));
            await primary.GetByLabel("Restore").ClickAsync();
            Assert.Equal("410", await primary.GetAttributeAsync("data-window-width"));
            Assert.True(failures.Count == 0, $"Page failures ({failures.Count}):\n" + string.Join("\n", failures));
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies group and settings contracts through the published browser module.</summary>
    [Fact]
    public async Task Group_and_settings_contracts_pass_in_real_browser()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions
                {
                    Channel = "chrome",
                    Headless = true
                });
            IPage page = await browser.NewPageAsync();
            await NavigateWhenReadyAsync(page, address);

            ILocator result = page.Locator("#contract-result");
            await result.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.WaitForFunctionAsync(
                "() => ['passed', 'failed'].includes(document.querySelector('#contract-result')?.dataset.status)");

            string? status = await result.GetAttributeAsync("data-status");
            string message = (await result.TextContentAsync()) ?? "Harness returned no result message.";
            Assert.True(status == "passed", message);

            await page.EvaluateAsync("() => history.replaceState({}, '', '/?scenario=reload')");
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.WaitForFunctionAsync(
                "() => ['passed', 'failed'].includes(document.querySelector('#contract-result')?.dataset.status)");

            status = await result.GetAttributeAsync("data-status");
            message = (await result.TextContentAsync()) ?? "Reload harness returned no result message.";
            Assert.True(status == "passed", message);
            Assert.Contains("survived a page reload", message, StringComparison.Ordinal);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies native rollback for failed writes and failed schema upgrades.</summary>
    [Fact]
    public async Task Failed_transaction_and_migration_preserve_prior_committed_state()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            await NavigateWhenReadyAsync(page, address);

            JsonElement result = await page.EvaluateAsync<JsonElement>(
                """
                async () => {
                    const module = await import('/_content/Kazo.Os.Infrastructure.Browser/indexedDb.js');
                    const createV1 = {
                        steps: [{
                            targetVersion: 1,
                            createObjectStores: [{
                                name: 'records',
                                keyPath: ['id'],
                                autoIncrement: false,
                                indexes: []
                            }]
                        }]
                    };

                    const rollbackDatabase = `kazoos-rollback-${crypto.randomUUID()}`;
                    await module.openDatabase(rollbackDatabase, 1, createV1);
                    let transactionRejected = false;
                    try {
                        await module.executeTransaction(
                            rollbackDatabase,
                            1,
                            ['records'],
                            'readwrite',
                            [
                                { kind: 'add', objectStoreName: 'records', value: { id: 'same' } },
                                { kind: 'add', objectStoreName: 'records', value: { id: 'same' } }
                            ]);
                    } catch {
                        transactionRejected = true;
                    }
                    const rollbackCount = (await module.executeTransaction(
                        rollbackDatabase,
                        1,
                        ['records'],
                        'readonly',
                        [{ kind: 'count', objectStoreName: 'records' }]))[0];
                    await module.deleteDatabase(rollbackDatabase);

                    const migrationDatabase = `kazoos-migration-${crypto.randomUUID()}`;
                    await module.openDatabase(migrationDatabase, 1, createV1);
                    await module.executeTransaction(
                        migrationDatabase,
                        1,
                        ['records'],
                        'readwrite',
                        [{ kind: 'add', objectStoreName: 'records', value: { id: 'committed' } }]);
                    let migrationRejected = false;
                    try {
                        await module.openDatabase(migrationDatabase, 2, {
                            steps: [{
                                targetVersion: 2,
                                createIndexes: [{
                                    objectStoreName: 'missing-store',
                                    index: { name: 'invalid', keyPath: ['value'], unique: false }
                                }]
                            }]
                        });
                    } catch {
                        migrationRejected = true;
                    }
                    await module.openDatabase(migrationDatabase, 1, createV1);
                    const retainedCount = (await module.executeTransaction(
                        migrationDatabase,
                        1,
                        ['records'],
                        'readonly',
                        [{ kind: 'count', objectStoreName: 'records' }]))[0];
                    await module.deleteDatabase(migrationDatabase);

                    return { transactionRejected, rollbackCount, migrationRejected, retainedCount };
                }
                """);

            Assert.True(result.GetProperty("transactionRejected").GetBoolean());
            Assert.Equal(0, result.GetProperty("rollbackCount").GetInt32());
            Assert.True(result.GetProperty("migrationRejected").GetBoolean());
            Assert.Equal(1, result.GetProperty("retainedCount").GetInt32());
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies that two tabs cannot both commit the same expected revision.</summary>
    [Fact]
    public async Task Multi_tab_revision_conflict_allows_exactly_one_commit()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IBrowserContext context = await browser.NewContextAsync();
            IPage firstPage = await context.NewPageAsync();
            IPage secondPage = await context.NewPageAsync();
            await Task.WhenAll(
                NavigateWhenReadyAsync(firstPage, $"{address}/?scenario=idle"),
                NavigateWhenReadyAsync(secondPage, $"{address}/?scenario=idle"));

            string databaseName = $"kazoos-tabs-{Guid.NewGuid():N}";
            const string setupScript = """
                async ({ databaseName }) => {
                    const module = await import('/_content/Kazo.Os.Infrastructure.Browser/indexedDb.js');
                    await module.openDatabase(databaseName, 1, {
                        steps: [{
                            targetVersion: 1,
                            createObjectStores: [{
                                name: 'records',
                                keyPath: ['id'],
                                autoIncrement: false,
                                indexes: []
                            }]
                        }]
                    });
                    await module.executeTransaction(databaseName, 1, ['records'], 'readwrite', [{
                        kind: 'add',
                        objectStoreName: 'records',
                        value: { id: 'shared', revision: 1, value: 'initial' }
                    }]);
                }
                """;
            await firstPage.EvaluateAsync(setupScript, new { databaseName });

            const string updateScript = """
                async ({ databaseName, value }) => {
                    const module = await import('/_content/Kazo.Os.Infrastructure.Browser/indexedDb.js');
                    await module.openDatabase(databaseName, 1, { steps: [] });
                    const result = await module.executeTransaction(
                        databaseName,
                        1,
                        ['records'],
                        'readwrite',
                        [{
                            kind: 'compareAndPut',
                            objectStoreName: 'records',
                            key: 'shared',
                            value: { id: 'shared', revision: 2, value },
                            compareProperty: 'revision',
                            expectedValue: 1
                        }]);
                    return result[0].committed;
                }
                """;
            Task<bool> firstUpdate = firstPage.EvaluateAsync<bool>(
                updateScript,
                new { databaseName, value = "first" });
            Task<bool> secondUpdate = secondPage.EvaluateAsync<bool>(
                updateScript,
                new { databaseName, value = "second" });
            bool[] commits = await Task.WhenAll(firstUpdate, secondUpdate);

            Assert.Single(commits, committed => committed);
            JsonElement finalRecord = await firstPage.EvaluateAsync<JsonElement>(
                """
                async ({ databaseName }) => {
                    const module = await import('/_content/Kazo.Os.Infrastructure.Browser/indexedDb.js');
                    const result = await module.executeTransaction(
                        databaseName,
                        1,
                        ['records'],
                        'readonly',
                        [{ kind: 'get', objectStoreName: 'records', key: 'shared' }]);
                    await module.deleteDatabase(databaseName);
                    return result[0];
                }
                """,
                new { databaseName });
            Assert.Equal(2, finalRecord.GetProperty("revision").GetInt32());
            Assert.Contains(
                finalRecord.GetProperty("value").GetString(),
                new[] { "first", "second" });
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies native Chromium quota failure reaches the recoverable C# exception.</summary>
    [Fact]
    public async Task Quota_exhaustion_is_reported_as_recoverable_storage_failure()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=idle");

            ICDPSession session = await context.NewCDPSessionAsync(page);
            await session.SendAsync(
                "Storage.overrideQuotaForOrigin",
                new Dictionary<string, object>
                {
                    ["origin"] = address,
                    ["quotaSize"] = 1
                });

            await page.GotoAsync(
                $"{address}/?scenario=quota",
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            ILocator result = page.Locator("#contract-result");
            await page.WaitForFunctionAsync(
                "() => ['passed', 'failed'].includes(document.querySelector('#contract-result')?.dataset.status)");

            string? status = await result.GetAttributeAsync("data-status");
            string message = (await result.TextContentAsync()) ?? "Quota harness returned no result message.";
            Assert.True(status == "passed", message);
            Assert.Contains("BrowserStorageQuotaException", message, StringComparison.Ordinal);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies representative MudBlazor wrappers on desktop and mobile viewports.</summary>
    [Fact]
    public async Task Platform_complex_controls_are_interactive_accessible_and_responsive()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 800 }
            });
            List<string> browserFailures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    browserFailures.Add($"console: {message.Text}");
                }
            };
            await NavigateWhenReadyAsync(page, $"{address}/?scenario=idle");
            // Startup probing may intentionally encounter a refused connection;
            // collect only failures after the harness has become reachable.
            page.RequestFailed += (_, request) =>
                browserFailures.Add($"network: {request.Method} {request.Url}");
            await page.GetByRole(AriaRole.Heading, new() { Name = "Control surface" }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Actions" }).ClickAsync();
            await page.GetByRole(AriaRole.Menuitem, new() { Name = "Audit storage" }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Status).Last)
                .ToContainTextAsync("Audit storage selected.");

            await page.GetByRole(AriaRole.Tab, new() { Name = "Operator" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Validate" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Operator form requires attention."))
                .ToBeVisibleAsync();
            await page.GetByLabel("Operator name").FillAsync("root");
            await page.GetByRole(AriaRole.Button, new() { Name = "Validate" }).ClickAsync();
            await Assertions.Expect(page.GetByText("Operator form is valid."))
                .ToBeVisibleAsync();

            await page.SetViewportSizeAsync(375, 812);
            bool hasHorizontalOverflow = await page.EvaluateAsync<bool>(
                "() => document.documentElement.scrollWidth > document.documentElement.clientWidth");
            Assert.False(hasHorizontalOverflow);
            await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Control surface" }))
                .ToBeVisibleAsync();

            string screenshotPath = Path.Combine(
                Path.GetTempPath(),
                $"kazoos-platform-ui-{Guid.NewGuid():N}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = screenshotPath,
                FullPage = true
            });
            Assert.True(new FileInfo(screenshotPath).Length > 0);
            File.Delete(screenshotPath);
            Assert.Empty(browserFailures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    private static async Task NavigateWhenReadyAsync(IPage page, string address)
    {
        Exception? lastFailure = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await page.GotoAsync(address, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                return;
            }
            catch (PlaywrightException exception)
            {
                lastFailure = exception;
                await Task.Delay(100);
            }
        }

        throw new InvalidOperationException("The browser harness did not become ready.", lastFailure);
    }

    /// <summary>Verifies full-screen key mapping, rendering, focus, editing, and cleanup in Chromium.</summary>
    [Fact]
    public async Task Terminal_full_screen_adapter_edits_and_restores_the_regular_screen()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            page.PageError += (_, error) => failures.Add($"page: {error}");

            await NavigateWhenReadyAsync(page, $"{address}/?scenario=terminal-full-screen");
            page.RequestFailed += (_, request) => failures.Add($"network: {request.Method} {request.Url}");
            ILocator screen = page.Locator("[data-terminal-full-screen]");
            await screen.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal("textbox", await screen.GetAttributeAsync("role"));
            Assert.Equal("Interactive terminal editor test", await screen.GetAttributeAsync("aria-label"));

            await screen.PressAsync("a");
            await screen.PressAsync("b");
            await screen.PressAsync("Backspace");
            await screen.PressAsync("c");
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#terminal-full-screen-result')?.textContent === 'ac'");
            Assert.Contains("ac", await screen.InnerTextAsync(), StringComparison.Ordinal);

            await screen.PressAsync("Control+x");
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#terminal-full-screen-result')?.dataset.status === 'exited'");
            Assert.Equal(0, await page.Locator("[data-terminal-full-screen]").CountAsync());
            Assert.Empty(failures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>
    /// Verifies the reusable VirtualKeyboard/VirtualKeyboardHost package in a real browser: on-screen
    /// taps reach the full-screen terminal through the same TerminalKeyEvent pipeline real key
    /// presses use, and no native input/contenteditable element exists anywhere to ever trigger a
    /// platform on-screen keyboard.
    /// </summary>
    [Fact]
    public async Task Virtual_keyboard_drives_the_full_screen_terminal_without_a_native_input()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            page.PageError += (_, error) => failures.Add($"page: {error}");

            await NavigateWhenReadyAsync(page, $"{address}/?scenario=virtual-keyboard-terminal");
            page.RequestFailed += (_, request) => failures.Add($"network: {request.Method} {request.Url}");

            Assert.Equal(0, await page.Locator("input, [contenteditable]").CountAsync());

            ILocator screen = page.Locator("[data-terminal-full-screen]");
            await screen.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            await page.GetByRole(AriaRole.Button, new() { Name = "a", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "b", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "⌫", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "c", Exact = true }).ClickAsync();
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#virtual-keyboard-terminal-result')?.textContent === 'ac'");
            Assert.Contains("ac", await screen.InnerTextAsync(), StringComparison.Ordinal);

            await page.GetByRole(AriaRole.Button, new() { Name = "Ctrl", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "x", Exact = true }).ClickAsync();
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#virtual-keyboard-terminal-result')?.dataset.status === 'exited'");
            Assert.Equal(0, await page.Locator("[data-terminal-full-screen]").CountAsync());
            Assert.Equal(0, await page.Locator("input, [contenteditable]").CountAsync());
            Assert.Empty(failures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies the locally bundled Monaco editor, C# state callback, mode switch, and disposal.</summary>
    [Fact]
    public async Task Code_editor_local_bundle_edits_switches_mode_and_disposes_cleanly()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            page.PageError += (_, error) => failures.Add($"page: {error}");

            await NavigateWhenReadyAsync(page, $"{address}/?scenario=code-editor");
            page.RequestFailed += (_, request) => failures.Add($"network: {request.Method} {request.Url}");
            ILocator editor = page.Locator(".monaco-editor");
            // Monaco's own bundle is ~3.6MB plus a ~6.7MB TS worker — first paint can exceed the
            // default 30s wait under CPU contention (e.g. other test suites/builds running
            // concurrently on the same machine), so this one gets a longer explicit budget like
            // the rest of this suite's slower waits already have.
            await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
            // Monaco's editable surface is a `div.native-edit-context` (Chrome's EditContext API),
            // not a real <textarea>/contenteditable — FillAsync sets .value directly, which the
            // EditContext-backed editor never observes, so content must go through real key events.
            ILocator content = page.Locator(".native-edit-context");
            Assert.Equal("textbox", await content.GetAttributeAsync("role"));

            // .native-edit-context is an accessibility-only proxy for Chrome's EditContext API —
            // it sits underneath Monaco's own rendered text lines, so it can't be the click target
            // (Playwright's actionability check finds .view-line intercepting the pointer). Click
            // the visible editor surface to establish focus, same as a real user would, then type.
            await editor.ClickAsync();
            // PressSequentially types at the caret without clearing existing content (unlike the
            // old FillAsync, which replaced .value outright) — select-all first so the assertion
            // below checks the whole document, not the seed comment with this appended to it.
            await content.PressAsync("ControlOrMeta+A");
            await content.PressSequentiallyAsync("const answer = 42;");
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#code-editor-result')?.textContent === 'const answer = 42;'");
            Assert.Equal("edited", await page.Locator("#code-editor-result").GetAttributeAsync("data-status"));

            await page.GetByRole(AriaRole.Button, new() { Name = "Use JSON mode" }).ClickAsync();
            await page.WaitForFunctionAsync(
                "() => document.querySelector('#code-editor-result')?.dataset.mode === 'Json'");

            await page.GetByRole(AriaRole.Button, new() { Name = "Dispose editor" }).ClickAsync();
            await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            Assert.Equal("disposed", await page.Locator("#code-editor-result").GetAttributeAsync("data-status"));
            Assert.Empty(failures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies the published browser harness renders one RGBA image model through draw, history, crop, and pan.</summary>
    [Fact]
    public async Task Kazo_paint_canvas_draws_undoes_redoes_crops_and_pans()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) => { if (message.Type == "error") failures.Add($"console: {message.Text}"); };
            page.PageError += (_, error) => failures.Add($"page: {error}");

            await NavigateWhenReadyAsync(page, $"{address}/?scenario=kazo-paint");
            ILocator canvas = page.Locator("#kazo-paint-canvas");
            await canvas.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.WaitForFunctionAsync("() => document.querySelector('#kazo-paint-result')?.dataset.status === 'ready'");

            await page.GetByRole(AriaRole.Button, new() { Name = "Draw pixel" }).ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('#kazo-paint-result')?.dataset.status === 'drawn'");
            Assert.Equal(255, await page.EvaluateAsync<int>("() => document.querySelector('#kazo-paint-canvas').getContext('2d').getImageData(2, 2, 1, 1).data[0]"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Undo" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Redo" }).ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('#kazo-paint-result')?.dataset.status === 'redone'");
            await page.GetByRole(AriaRole.Button, new() { Name = "Crop" }).ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('#kazo-paint-result')?.dataset.size === '8x8'");
            await page.GetByRole(AriaRole.Button, new() { Name = "Pan" }).ClickAsync();
            await page.WaitForFunctionAsync("() => document.querySelector('#kazo-paint-result')?.dataset.pan === '5,-3'");
            Assert.Empty(failures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Runs axe-core against the representative shell, window, and dialog surfaces.</summary>
    [Fact]
    public async Task Representative_platform_surfaces_have_no_axe_violations()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();

            foreach (string scenario in new[] { "idle", "window", "dialog", "terminal-full-screen", "code-editor", "kazo-paint", "taskbar" })
            {
                await NavigateWhenReadyAsync(page, $"{address}/?scenario={scenario}");
                var result = await page.RunAxe();
                var blocking = result.Violations?
                    .Where(item => string.Equals(item.Impact, "serious", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(item.Impact, "critical", StringComparison.OrdinalIgnoreCase))
                    .ToArray() ?? [];
                Assert.True(
                    blocking.Length == 0,
                    $"serious/critical axe violations for '{scenario}': {string.Join(", ", blocking.Select(item => item.Id))}");
            }
        }
        finally
        {
            StopProcess(server);
        }
    }

    /// <summary>Verifies the exported <c>Kazo.Os.Taskbar.Blazor.Taskbar</c> renders from host contracts,
    /// reacts to window/notification/clock/session changes routed only through those contracts, and hides
    /// each optional zone cleanly when its contract is withheld (EXT-WIN-007/008).</summary>
    [Fact]
    public async Task Taskbar_reacts_to_contracts_and_hides_optional_zones_cleanly()
    {
        string solutionDirectory = FindSolutionDirectory();
        int port = ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = StartHarness(solutionDirectory, address);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, 
                new BrowserTypeLaunchOptions { Channel = "chrome", Headless = true });
            IPage page = await browser.NewPageAsync();
            List<string> failures = [];
            page.Console += (_, message) =>
            {
                if (message.Type == "error") failures.Add($"console: {message.Text}");
            };
            page.PageError += (_, error) => failures.Add($"page: {error}");

            await NavigateWhenReadyAsync(page, $"{address}/?scenario=taskbar");
            page.RequestFailed += (_, request) => failures.Add($"network: {request.Method} {request.Url}");

            ILocator scenarioLog = page.Locator("[data-scenario-log]");
            ILocator window1 = page.GetByRole(AriaRole.Button, new() { Name = "Scenario Window 1 (focused)" });
            ILocator window2 = page.GetByRole(AriaRole.Button, new() { Name = "Scenario Window 2 (background)" });
            await window1.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await window2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            ILocator launcher = page.GetByLabel("App launcher");
            Assert.Equal("false", await launcher.GetAttributeAsync("aria-expanded"));
            await page.GetByLabel("System clock, notifications, and calendar")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await Assertions.Expect(page.Locator(".clock-text")).ToHaveTextAsync("09:00:00");
            ILocator logout = page.GetByLabel("User session and logout");
            await logout.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // A background window click activates it; the previously focused window becomes background.
            await window2.ClickAsync();
            window2 = page.GetByRole(AriaRole.Button, new() { Name = "Scenario Window 2 (focused)" });
            window1 = page.GetByRole(AriaRole.Button, new() { Name = "Scenario Window 1 (background)" });
            await window2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await window1.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // A focused window click minimizes it.
            await window2.ClickAsync();
            window2 = page.GetByRole(AriaRole.Button, new() { Name = "Scenario Window 2 (minimized)" });
            await window2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Contains("is-minimized", await window2.GetAttributeAsync("class"), StringComparison.Ordinal);

            // A minimized window click restores and re-focuses it.
            await window2.ClickAsync();
            window2 = page.GetByRole(AriaRole.Button, new() { Name = "Scenario Window 2 (focused)" });
            await window2.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // A scenario-level control adds a third window directly through the window source contract.
            await page.GetByRole(AriaRole.Button, new() { Name = "Add window" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Scenario Window 3 (background)" })
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // The clock panel source pushes open/closed state without a taskbar-initiated
            // command, and its content — supplied entirely by the host through ClockPanelContent —
            // renders inside the container the taskbar itself owns for anchoring only.
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle clock panel", Exact = true }).ClickAsync();
            await page.Locator("[data-scenario-clock-panel]")
                .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            await page.GetByRole(AriaRole.Button, new() { Name = "Tick clock" }).ClickAsync();
            await Assertions.Expect(page.Locator(".clock-text")).ToHaveTextAsync("09:00:01");

            // The launcher trigger toggles open/closed state from the host-supplied contract.
            await launcher.ClickAsync();
            Assert.Equal("true", await launcher.GetAttributeAsync("aria-expanded"));

            // Logout routes through the host-supplied session commands contract.
            await logout.ClickAsync();
            await Assertions.Expect(scenarioLog).ToHaveTextAsync("Logout requested");

            // Each optional zone disappears cleanly when its contract is withheld, and returns when restored.
            // The "Running applications" <nav> wrapper itself is unconditional (only the window buttons
            // inside are gated), so an empty nav collapses to zero size (hidden) rather than detaching.
            ILocator runningApplications = page.GetByRole(AriaRole.Navigation, new() { Name = "Running applications" });
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle window source" }).ClickAsync();
            await runningApplications.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle window source" }).ClickAsync();
            await runningApplications.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle launcher" }).ClickAsync();
            await launcher.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle launcher" }).ClickAsync();
            await launcher.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle status source" }).ClickAsync();
            await page.Locator(".taskbar-clock").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle status source" }).ClickAsync();
            await page.Locator(".taskbar-clock").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            // The clock button itself stays visible (StatusSource, not ClockPanelSource, controls
            // that) — withholding ClockPanelSource only removes the panel it would otherwise show.
            // The panel from the earlier "Toggle clock panel" click is still open at this point.
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle clock panel source" }).ClickAsync();
            await page.Locator("[data-scenario-clock-panel]").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle clock panel source" }).ClickAsync();
            await page.Locator("[data-scenario-clock-panel]").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle session commands" }).ClickAsync();
            await logout.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
            await page.GetByRole(AriaRole.Button, new() { Name = "Toggle session commands" }).ClickAsync();
            await logout.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

            Assert.Empty(failures);
        }
        finally
        {
            StopProcess(server);
        }
    }

    private static Process StartHarness(string solutionDirectory, string address)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = solutionDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("Tests/Kazo.Os.BrowserHarness.Tests/Kazo.Os.BrowserHarness.Tests.csproj");
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(address);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the browser harness.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kazo.Os.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate Kazo.Os.sln.");
    }

    private static int ReservePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void StopProcess(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }
    }
}
