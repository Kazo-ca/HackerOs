using Microsoft.Playwright;

namespace HackerOs.TrimmedPublish.E2E.Tests;

public sealed partial class TrimmedPublishTests
{
    /// <summary>
    /// ADR 0196 on the published host, which serves <c>whats-new.json</c>: a new account gets the
    /// what's new notification of the latest version (ADR 0186 §5), so the clock carries the unread
    /// dot and gives the count in its tooltip; the Applications button has none. Opening the clock
    /// panel reads it, and its View button opens Settings and dismisses it.
    /// </summary>
    [TrimmedPublishFact]
    public async Task Trimmed_publish_shows_unread_notifications_next_to_the_clock()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);

            ILocator clock = page.Locator("footer.taskbar-container .taskbar-clock");
            ILocator dot = clock.Locator(".clock-attention");
            await Assertions.Expect(dot).ToHaveCountAsync(1, new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(clock).ToHaveAttributeAsync("title", UnreadClockName());
            await Assertions.Expect(page.Locator(".launcher-attention")).ToHaveCountAsync(0);
            Log($"clock with unread notifications: {await clock.GetAttributeAsync("title")}");
            await SaveEvidenceAsync(page, settings, "clock-dot");

            await clock.ClickAsync();
            ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            ILocator view = panel.GetByRole(AriaRole.Button, new() { Name = "View" });
            await Assertions.Expect(view).ToBeVisibleAsync();
            await Assertions.Expect(dot).ToHaveCountAsync(0);
            await Assertions.Expect(clock).ToHaveAttributeAsync("title", "System clock, notifications, and calendar");

            // Read, the notification stays in the list until it is acted on.
            await clock.ClickAsync();
            await clock.ClickAsync();
            await Assertions.Expect(view).ToBeVisibleAsync();
            await Assertions.Expect(dot).ToHaveCountAsync(0);

            await view.ClickAsync();
            await Assertions.Expect(page.Locator(".wn-summary")).ToContainTextAsync("since your last visit");
            await SaveEvidenceAsync(page, settings, "whats-new-page");
            await clock.ClickAsync();
            await Assertions.Expect(panel.GetByRole(AriaRole.Button, new() { Name = "View" })).ToHaveCountAsync(0);
            Log("opening the panel read the notification; View opened Settings and dismissed it.");
            session.AssertNoErrors("unread notifications");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "notifications");
            throw;
        }
    }

    private async Task SaveEvidenceAsync(IPage page, TrimmedPublishSettings settings, string name)
    {
        if (settings.ArtifactsDirectory is null)
        {
            return;
        }

        Directory.CreateDirectory(settings.ArtifactsDirectory);
        string path = Path.Combine(settings.ArtifactsDirectory, $"trimmed-{name}.png");
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = path });
        Log($"screenshot: {path}");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^System clock, notifications, and calendar: (1 unread notification|\d+ unread notifications)$")]
    private static partial System.Text.RegularExpressions.Regex UnreadClockName();
}
