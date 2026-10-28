using HackerOs.Platform.Core.Shell;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Settings;

namespace HackerOs.Platform.Core.Tests.Shell;

/// <summary>ADR 0179 §7, ADR 0180: the <c>[screens]</c> section of <c>screens.config</c>.</summary>
public sealed class ScreensSettingsTests
{
    [Theory]
    [InlineData("1920,0,1920,1080", 1920, 0, 1920, 1080)]
    [InlineData(" -1280 , 24 , 1280 , 1000 ", -1280, 24, 1280, 1000)]
    public void A_placement_reads_left_top_width_height(string text, int left, int top, int width, int height)
    {
        Assert.True(ScreenGeometry.TryParse(text, out ScreenGeometry geometry));
        Assert.Equal(new ScreenGeometry(left, top, width, height), geometry);
        Assert.Equal(geometry, ScreenGeometry.TryParse(geometry.ToString(), out ScreenGeometry again) ? again : default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,100,1080")]
    [InlineData("0,0,1920,80")]
    [InlineData("0,0,1920,1080,5")]
    public void A_bad_placement_is_rejected(string? text)
    {
        Assert.False(ScreenGeometry.TryParse(text, out _));
    }

    [Fact]
    public async Task The_user_file_gives_the_screens_to_restore_and_their_places()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsRootAsync(
            "/home/alice/.config/hackeros/screens.config",
            "[screens]\nopen = 3, 2, 3, 1, 12, x\n2 = 1920,0,1920,1080\n3 = 0,0,10,10\n");

        LayeredConfigSnapshot snapshot = await harness.Settings.ReadAsync(ScreensSettingsDocuments.Document);

        Assert.Equal([3, 2], ScreensSettingsDocuments.ReadOpenScreens(snapshot));
        KeyValuePair<int, ScreenGeometry> only = Assert.Single(ScreensSettingsDocuments.ReadGeometries(snapshot));
        Assert.Equal(2, only.Key);
        Assert.Equal(new ScreenGeometry(1920, 0, 1920, 1080), only.Value);
    }

    [Fact]
    public async Task Writing_the_open_screens_and_a_place_round_trips()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);

        await harness.Settings.SetManyAsync(
            ScreensSettingsDocuments.Document,
            [
                new(ScreensSettingsDocuments.OpenQualifiedKey, ScreensSettingsDocuments.FormatOpenScreens([2, 4])),
                new(ScreensSettingsDocuments.GeometryQualifiedKey(4), new ScreenGeometry(-1280, 0, 1280, 1024).ToString()),
            ]);

        string? content = await harness.ReadAsync("/home/alice/.config/hackeros/screens.config");
        Assert.NotNull(content);
        Assert.Contains("[screens]", content, StringComparison.Ordinal);
        LayeredConfigSnapshot snapshot = await harness.Settings.ReadAsync(ScreensSettingsDocuments.Document);
        Assert.Equal([2, 4], ScreensSettingsDocuments.ReadOpenScreens(snapshot));
        Assert.Equal(new ScreenGeometry(-1280, 0, 1280, 1024), ScreensSettingsDocuments.ReadGeometries(snapshot)[4]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Only_secondary_screen_numbers_have_a_place(int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreensSettingsDocuments.GeometryQualifiedKey(number));
    }
}
