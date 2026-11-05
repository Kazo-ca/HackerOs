using System.Diagnostics;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace Kazo.Os.UI.E2E.Tests;

/// <summary>
/// ADR 0104 section 6: a user writes <c>~/.config/locale.conf</c> in French, signs in again, and
/// finds the launcher, an app and a command in French — the platform texts, the translated app
/// names and the French satellite assemblies all loaded in Chromium.
/// </summary>
public sealed class FrenchSessionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_French_locale_conf_translates_the_launcher_an_app_and_a_command_at_the_next_sign_in()
    {
        string solutionDirectory = E2ESupport.FindSolutionDirectory();
        int port = E2ESupport.ReservePort();
        string address = $"http://127.0.0.1:{port}";
        using Process server = E2ESupport.StartHarness(solutionDirectory, address, testDemo: false, output);

        try
        {
            using IPlaywright playwright = await Playwright.CreateAsync();
            await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new BrowserTypeLaunchOptions { Headless = true });
            await using IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            E2ESupport.AttachDiagnostics(page, output);

            await E2ESupport.NavigateWhenReadyAsync(page, address, output);
            await E2ESupport.CreateLocalProfileAndReachDesktopAsync(page, output);

            // --- English session: choose French for the next sign-in, as Settings → Language would ---
            await E2ESupport.OpenAppAsync(page, "Terminal", output);
            ILocator rows = page.Locator("[data-terminal-panel] .xterm-rows").First;
            ILocator screen = page.Locator("[data-terminal-panel] .xterm-screen").First;
            await WaitForTextAsync(rows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
            await screen.ClickAsync();
            await page.Keyboard.TypeAsync("mkdir -p ~/.config && echo LANG=fr_FR.UTF-8 > ~/.config/locale.conf && echo saved-$LANG");
            await page.Keyboard.PressAsync("Enter");
            await WaitForTextAsync(rows, text => text.Contains("saved-en_US.UTF-8", StringComparison.Ordinal), "the saved locale file");
            output.WriteLine("[test] ~/.config/locale.conf written; the running session stays English.");

            // --- Sign in again: the system locale (English) greets, the user's locale takes over ---
            await page.ReloadAsync();
            await page.GetByLabel("Password", new() { Exact = true }).FillAsync(E2ESupport.Password, new() { Timeout = 60000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Start session", Exact = true }).ClickAsync();

            ILocator launcher = page.GetByRole(AriaRole.Button, new() { Name = "Lanceur d'applications", Exact = true });
            await launcher.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60000 });
            await launcher.ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher des applications" }).FillAsync("Calc");
            await page.GetByRole(AriaRole.Button, new() { Name = "Lancer Calculatrice", Exact = true }).ClickAsync(new() { Timeout = 20000 });
            output.WriteLine("[test] the launcher speaks French and names the Calculator « Calculatrice ».");

            // The Calculator is loaded lazily: its French satellite assembly must have been loaded too.
            ILocator memoryClear = page.Locator("#calc-mc");
            await memoryClear.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
            Assert.Equal("Effacer la mémoire", await memoryClear.GetAttributeAsync("aria-label"));

            // --- A command runs in a Web Worker and answers in the language of its process ---
            await launcher.ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Rechercher des applications" }).FillAsync("Terminal");
            await page.GetByRole(AriaRole.Button, new() { Name = "Lancer Terminal", Exact = true }).ClickAsync(new() { Timeout = 20000 });
            ILocator frenchRows = page.Locator("[data-terminal-panel] .xterm-rows").Last;
            ILocator frenchScreen = page.Locator("[data-terminal-panel] .xterm-screen").Last;
            await WaitForTextAsync(frenchRows, text => text.Contains('$', StringComparison.Ordinal), "the shell prompt");
            await frenchScreen.ClickAsync();
            await page.Keyboard.TypeAsync("mkdir; help nope; LANG=en_US.UTF-8 help nope");
            await page.Keyboard.PressAsync("Enter");
            string text = await WaitForTextAsync(
                frenchRows,
                t => t.Contains("no help topics match 'nope'", StringComparison.Ordinal),
                "the three answers");
            Assert.Contains("mkdir : opérande manquant", text, StringComparison.Ordinal);
            Assert.Contains("aucune rubrique d'aide ne correspond à « nope »", text, StringComparison.Ordinal);
            output.WriteLine("[test] commands answer in French, and in English under LANG=en_US.UTF-8.");
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
            text = (await rows.InnerTextAsync()).Replace(' ', ' ').Replace("\n", string.Empty, StringComparison.Ordinal);
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
