using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Appearance;

/// <summary>The shell's live catalog of <c>.theme</c> files (ADR 0103 §2.2).</summary>
public sealed class UserThemeCatalogServiceTests
{
    private static string ThemeText(string id, string name, string accent) =>
        $"[theme]\nid = {id}\nname = {name}\nbase = kazoos\n\n[tokens]\n--kos-accent = {accent}\n";

    [Fact]
    public async Task Reads_etc_and_the_user_folder_and_survives_a_reload()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/kazoos/themes/corporate.theme", ThemeText("corporate", "Corporate", "#0055aa"));
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.config/kazoos/themes/neon.theme", ThemeText("neon", "Neon", "#ff00ff"));

        await using (UserThemeCatalogService first = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings))
        {
            await first.InitializeAsync();
            Assert.Equal(["corporate", "neon"], first.Current.Themes.Select(theme => theme.Id).Order());
        }

        // A new service (the page reloaded) reads the same files: the saved theme still applies.
        await using UserThemeCatalogService reloaded = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await reloaded.InitializeAsync();
        ResolvedTheme resolved = reloaded.Resolve("neon", ThemePlatform.Desktop);
        Assert.False(resolved.IsFallback);
        Assert.Equal("#ff00ff", resolved.UserTheme!.Tokens.Single().Value);
    }

    [Fact]
    public async Task RefreshAsync_picks_up_an_edit_and_notifies()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await using UserThemeCatalogService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await service.InitializeAsync();
        Assert.Empty(service.Current.Themes);
        int notifications = 0;
        service.Changed += () => notifications++;

        await harness.WriteAsUserAsync("/home/alice/.config/kazoos/themes/neon.theme", ThemeText("neon", "Neon", "#ff00ff"));
        await service.RefreshAsync();

        Assert.Equal("neon", Assert.Single(service.Current.Themes).Id);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task A_broken_file_falls_back_to_the_default_theme()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.config/kazoos/themes/broken.theme", "[theme]\nid = broken\n");
        await using UserThemeCatalogService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await service.InitializeAsync();

        Assert.Empty(service.Current.Themes);
        Assert.Contains(service.Current.Diagnostics, diagnostic => diagnostic.IsError);
        Assert.True(service.Resolve("broken", ThemePlatform.Desktop).IsFallback);
    }
}
