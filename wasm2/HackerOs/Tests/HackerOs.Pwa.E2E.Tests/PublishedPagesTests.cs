using System.Reflection;
using Microsoft.Playwright;

namespace HackerOs.Pwa.E2E.Tests;

/// <summary>Checks the real combined Pages artifact, including legacy navigation under the root PWA worker.</summary>
public sealed class PublishedPagesTests
{
    private static readonly string HackerOsVersion = typeof(PublishedPagesTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "HackerOsVersion").Value!;

    [PagesArtifactFact]
    public async Task Wasm_and_legacy_load_independently_after_the_service_worker_takes_control()
    {
        string directory = Environment.GetEnvironmentVariable("HACKEROS_PAGES_ROOT")!;
        await using PublishedAppHost host = new();
        await host.StartAsync(directory, PublishedAppHost.ReservePort());
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await ChromiumLauncher.LaunchAsync(playwright, new() { Headless = true });
        IPage page = await browser.NewPageAsync();
        List<string> errors = [];
        page.PageError += (_, error) => errors.Add(error);
        await PwaTestSupport.NavigateWhenReadyAsync(page, host.Address);
        await PwaTestSupport.WaitForServiceWorkerControllerAsync(page);
        await PwaTestSupport.WaitForOnboardingAsync(page);
        await Assertions.Expect(page.Locator(".project-release")).ToContainTextAsync(HackerOsVersion);
        await Assertions.Expect(page.Locator(".project-information a").First).ToHaveAttributeAsync("href", "https://github.com/Kazo-ca/HackerOs");
        await Assertions.Expect(page.Locator(".legacy-notice")).ToHaveCountAsync(0);

        await PwaTestSupport.CreateLocalProfileAndReachDesktopAsync(page);
        await PwaTestSupport.OpenAppAsync(page, "Settings");
        await page.GetByRole(AriaRole.Tab, new() { Name = "About System", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".system-info-box")).ToContainTextAsync($"HackerOS {HackerOsVersion}");
        await Assertions.Expect(page.Locator(".system-info-box a")).ToHaveAttributeAsync("href", "https://github.com/Kazo-ca/HackerOs");

        await page.GotoAsync(host.Address + "/legacy/");
        await Assertions.Expect(page.Locator("#desktop")).ToBeVisibleAsync();
        Assert.DoesNotContain("blazor.webassembly", await page.ContentAsync(), StringComparison.Ordinal);
        await page.Locator("#start-menu-button").ClickAsync();
        await page.Locator("#settings-item").ClickAsync();
        await page.Locator(".nav-item[data-section='about']").ClickAsync();
        await Assertions.Expect(page.Locator("#about-section a")).ToHaveAttributeAsync("href", "https://github.com/Kazo-ca/HackerOs");
        await page.ReloadAsync();
        await Assertions.Expect(page.Locator("#desktop")).ToBeVisibleAsync();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}

/// <summary>Runs this artifact test only when the caller supplies an assembled Pages directory.</summary>
public sealed class PagesArtifactFactAttribute : FactAttribute
{
    /// <summary>Marks normal solution test runs as skipped; deployment validation opts in explicitly.</summary>
    public PagesArtifactFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HACKEROS_PAGES_ROOT")))
            Skip = "Set HACKEROS_PAGES_ROOT to the assembled Pages artifact to exercise both releases.";
    }
}
