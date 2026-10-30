using Microsoft.Playwright;

namespace HackerOs.TrimmedPublish.E2E.Tests;

public sealed partial class TrimmedPublishTests
{
    /// <summary>
    /// ADR 0197 on the published host, which serves <c>whats-new.json</c>: the what's new
    /// notification posted at a new account's sign-in (ADR 0186 §5) shows as a bubble above the
    /// taskbar, with its source, title and View button, leaves after about 6 s, and stays in the
    /// clock panel.
    /// </summary>
    [TrimmedPublishFact]
    public async Task Trimmed_publish_shows_a_new_notification_as_a_bubble_above_the_taskbar()
    {
        TrimmedPublishSettings settings = TrimmedPublishSettings.FromEnvironment()!;
        await using Session session = await Session.StartAsync(output);
        IPage page = session.Page;
        try
        {
            await NavigateWhenReadyAsync(page, settings.Url);
            await SignInAsync(page);

            ILocator stack = page.GetByRole(AriaRole.Region, new() { Name = "New notifications" });
            ILocator bubble = stack.Locator("article.notification-toast");
            await Assertions.Expect(bubble).ToHaveCountAsync(1, new() { Timeout = StepTimeoutMs });
            await Assertions.Expect(bubble.Locator(".notification-toast-source")).ToHaveTextAsync("HackerOS");
            await Assertions.Expect(bubble.GetByRole(AriaRole.Heading, new() { Name = "What's new" })).ToBeVisibleAsync();
            await Assertions.Expect(bubble.GetByRole(AriaRole.Button, new() { Name = "View" })).ToBeVisibleAsync();

            // Above the taskbar: the bubble ends where the taskbar starts, or higher.
            LocatorBoundingBoxResult? box = await bubble.BoundingBoxAsync();
            LocatorBoundingBoxResult? taskbar = await page.Locator("footer.taskbar-container").BoundingBoxAsync();
            Assert.NotNull(box);
            Assert.NotNull(taskbar);
            Assert.True(box.Y + box.Height <= taskbar.Y, $"bubble bottom {box.Y + box.Height} below taskbar top {taskbar.Y}");
            Log($"bubble above the taskbar at {box.X:0},{box.Y:0} ({box.Width:0}x{box.Height:0}).");
            await SaveToastEvidenceAsync(page, settings, "notification-bubble");

            // It leaves on its own (6 simulated seconds) and the panel still lists the notification.
            await Assertions.Expect(bubble).ToHaveCountAsync(0, new() { Timeout = StepTimeoutMs });
            await page.Locator("footer.taskbar-container .taskbar-clock").ClickAsync();
            ILocator panel = page.GetByRole(AriaRole.Dialog, new() { Name = "Notifications and calendar" });
            await Assertions.Expect(panel.GetByRole(AriaRole.Button, new() { Name = "View" })).ToBeVisibleAsync();
            Log("the bubble left by itself; the notification stays in the clock panel.");
            session.AssertNoErrors("notification bubble");
        }
        catch
        {
            await SaveScreenshotAsync(page, settings, "notification-bubble");
            throw;
        }
    }

    private async Task SaveToastEvidenceAsync(IPage page, TrimmedPublishSettings settings, string name)
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
}
