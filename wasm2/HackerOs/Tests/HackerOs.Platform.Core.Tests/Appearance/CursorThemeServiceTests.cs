using HackerOs.Platform.Core.Appearance;
using HackerOs.Platform.Core.Tests.FileSystem.Virtual;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.App.Abstractions;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;

namespace HackerOs.Platform.Core.Tests.Appearance;

/// <summary>The shell's live cursor theme (ADR 0205 §3, §4).</summary>
public sealed class CursorThemeServiceTests
{
    private const string Index = "[cursor-theme]\nid = corporate\nname = Corporate\n\n[cursors]\ndefault = arrow.png 2 1\npointer = hand.png 40 0\n";

    [Fact]
    public async Task A_fresh_machine_ships_cursors_config_with_the_system_theme()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        using CursorThemeService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await service.InitializeAsync();

        Assert.Contains("\ntheme=system\n", await harness.ReadAsync("/etc/hackeros/cursors.config"), StringComparison.Ordinal);
        Assert.Same(CursorThemeCatalog.System, service.Current);
    }

    [Fact]
    public async Task The_user_layer_chooses_a_built_in_theme_and_a_change_notifies()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        using CursorThemeService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await service.InitializeAsync();
        int notifications = 0;
        service.Changed += () => notifications++;

        await harness.WriteAsUserAsync("/home/alice/.config/hackeros/cursors.config", "theme=hackeros-neon\n");
        await service.RefreshAsync();

        Assert.Equal(CursorThemeCatalog.NeonId, service.Current.Id);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task A_theme_folder_of_etc_applies_its_checked_images_and_skips_a_bad_one()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/hackeros/cursors/corporate/cursor.theme", Index);
        await WriteBytesAsRootAsync(harness, "/etc/hackeros/cursors/corporate/arrow.png", CursorThemeFileTests.Png(32, 32));
        // The hot spot (40, 0) is outside this 32 x 32 hand: refused, the host's pointer stays.
        await WriteBytesAsRootAsync(harness, "/etc/hackeros/cursors/corporate/hand.png", CursorThemeFileTests.Png(32, 32));
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.config/hackeros/cursors.config", "theme=corporate\n");

        using CursorThemeService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await service.InitializeAsync();

        Assert.Equal("corporate", service.Current.Id);
        Assert.Equal(ThemeOrigin.System, service.Current.Origin);
        CursorImage arrow = Assert.Single(service.Current.Images).Value;
        Assert.StartsWith("data:image/png;base64,", arrow.Url, StringComparison.Ordinal);
        Assert.Equal((2, 1), (arrow.HotspotX, arrow.HotspotY));
        Assert.Null(service.Current.Resolve(CursorNames.Pointer));
    }

    [Fact]
    public async Task An_id_that_names_no_theme_keeps_the_host_cursors()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.config/hackeros/cursors.config", "theme=deleted-theme\n");

        using CursorThemeService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await service.InitializeAsync();

        Assert.Same(CursorThemeCatalog.System, service.Current);
    }

    private static async Task WriteBytesAsRootAsync(SettingsFilesHarness harness, string path, byte[] bytes)
    {
        await harness.WriteAsRootAsync(path, string.Empty);
        FileSystemMutationResult write = await harness.CreateRootGateway().WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse(path)), new VirtualFileSystemFixture.BytesSource(bytes));
        Assert.True(write.Succeeded, write.Transaction.Error?.ToString());
    }
}
