using System.Text;
using HackerOs.Platform.Core.Appearance;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions;

namespace HackerOs.Platform.Core.Tests.Appearance;

/// <summary>Wallpapers (ADR 0202): the "data only" rules, the keys of <c>appearance.config</c> and the shell's service.</summary>
public sealed class WallpaperTests
{
    /// <summary>The smallest header <see cref="WallpaperCatalog.TryDetectMediaType"/> reads as WebP; every byte is valid UTF-8.</summary>
    private const string WebpHeader = "RIFF\u0010\0\0\0WEBPVP8 ";

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 }, "image/jpeg")]
    public void The_media_type_is_read_from_the_bytes(byte[] header, string expected)
    {
        Assert.True(WallpaperCatalog.TryDetectMediaType(header, out string mediaType));
        Assert.Equal(expected, mediaType);
        Assert.True(WallpaperCatalog.TryDetectMediaType(Encoding.UTF8.GetBytes(WebpHeader), out mediaType));
        Assert.Equal("image/webp", mediaType);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("GIF89a")]
    [InlineData("RIFF\u0010\0\0\0WAVE")]
    [InlineData("")]
    public void A_file_that_is_not_png_jpeg_or_webp_is_refused_whatever_its_name(string content)
    {
        Assert.False(WallpaperCatalog.TryDetectMediaType(Encoding.UTF8.GetBytes(content), out _));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("builtin:aurora", true)]
    [InlineData("builtin:unknown", false)]
    [InlineData("/home/alice/Pictures/beach.JPG", true)]
    [InlineData("/usr/share/hackeros/wallpapers/city.webp", true)]
    [InlineData("/home/alice/Pictures/logo.svg", false)]
    [InlineData("Pictures/beach.png", false)]
    [InlineData("https://example.com/a.png", false)]
    public void Only_empty_a_built_in_or_an_absolute_image_path_is_a_valid_value(string value, bool valid)
    {
        Assert.Equal(valid, WallpaperCatalog.IsValidValue(value));
    }

    [Fact]
    public void Built_ins_fits_and_folders_follow_the_ADR()
    {
        Assert.Equal(["hacker-grid", "circuit", "aurora", "nebula"], WallpaperCatalog.BuiltIn.Select(static wallpaper => wallpaper.Id));
        Assert.All(WallpaperCatalog.BuiltIn, static wallpaper =>
        {
            Assert.Equal($"_content/HackerOs.Theming.Blazor/wallpapers/{wallpaper.Id}.webp", wallpaper.AssetPath);
            Assert.True(WallpaperCatalog.TryGetBuiltIn(wallpaper.Value, out BuiltInWallpaper found));
            Assert.Same(wallpaper, found);
        });
        Assert.Equal(["fill", "fit", "stretch", "center", "tile"], WallpaperCatalog.FitValues);
        Assert.All(Enum.GetValues<WallpaperFit>(), static fit => Assert.Equal(fit, WallpaperCatalog.ParseFit(WallpaperCatalog.FormatFit(fit))));
        Assert.Equal(WallpaperFit.Fill, WallpaperCatalog.ParseFit("FILL!"));
        Assert.Equal("/home/alice/.local/share/hackeros/wallpapers", WallpaperCatalog.GetUserDirectory("/home/alice/"));
        Assert.Equal("/home/alice/.local/share/hackeros/theme-packs", WallpaperCatalog.GetUserThemePacksDirectory("/home/alice"));
    }

    [Fact]
    public void Appearance_declares_the_wallpaper_keys_with_the_theme_background_by_default()
    {
        IReadOnlyDictionary<string, SettingFieldDeclaration> fields = AppearanceSettingsDocuments.Schema.Fields;
        Assert.Equal(string.Empty, fields["wallpaper"].DefaultValue);
        Assert.Equal("fill", fields["wallpaperFit"].DefaultValue);
        Assert.Equal(WallpaperCatalog.FitValues, fields["wallpaperFit"].AllowedValues);
        for (int number = 2; number <= 9; number++)
        {
            Assert.Equal(string.Empty, fields[$"wallpaperScreen{number}"].DefaultValue);
        }

        Assert.False(fields.ContainsKey("wallpaperScreen1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppearanceSettingsDocuments.WallpaperScreenKey(10));
    }

    [Fact]
    public async Task Without_a_wallpaper_every_screen_keeps_the_theme_background()
    {
        SettingsFilesHarness harness = await SignedInAsync();
        using WallpaperService service = new(harness.Settings, harness.FileSystem, harness.Session, harness.Clock);
        await service.EnsureInitializedAsync();

        Assert.Null(await service.GetImageAsync(1));
        Assert.Null(await service.GetImageAsync(2));
    }

    [Fact]
    public async Task A_built_in_wallpaper_is_a_static_file_and_a_screen_without_its_own_uses_screen_1()
    {
        SettingsFilesHarness harness = await SignedInAsync();
        await SetAsync(harness, ("wallpaper", "builtin:nebula"), ("wallpaperFit", "tile"));
        using WallpaperService service = new(harness.Settings, harness.FileSystem, harness.Session, harness.Clock);
        await service.EnsureInitializedAsync();

        WallpaperImage image = Assert.IsType<WallpaperImage>(await service.GetImageAsync(1));
        Assert.Equal("_content/HackerOs.Theming.Blazor/wallpapers/nebula.webp", image.AssetPath);
        Assert.Null(image.Content);
        Assert.Equal(WallpaperFit.Tile, image.Fit);
        Assert.Equal("builtin:nebula", (await service.GetImageAsync(3))!.Value);
    }

    [Fact]
    public async Task A_file_is_read_as_the_user_and_a_screen_can_have_its_own()
    {
        SettingsFilesHarness harness = await SignedInAsync();
        await harness.WriteAsUserAsync("/home/alice/Pictures/beach.webp", WebpHeader);
        await SetAsync(harness, ("wallpaper", "builtin:aurora"), ("wallpaperScreen2", "/home/alice/Pictures/beach.webp"));
        using WallpaperService service = new(harness.Settings, harness.FileSystem, harness.Session, harness.Clock);
        await service.EnsureInitializedAsync();

        WallpaperImage second = Assert.IsType<WallpaperImage>(await service.GetImageAsync(2));
        Assert.Equal("image/webp", second.MediaType);
        Assert.Equal(Encoding.UTF8.GetBytes(WebpHeader), second.Content);
        Assert.Null(second.AssetPath);
        Assert.Equal("builtin:aurora", (await service.GetImageAsync(1))!.Value);
        Assert.Equal("builtin:aurora", (await service.GetImageAsync(4))!.Value);
    }

    [Theory]
    [InlineData("/home/alice/Pictures/missing.png")]
    [InlineData("/home/alice/Pictures/fake.png")]
    [InlineData("/home/alice/Pictures/logo.svg")]
    public async Task A_file_that_cannot_be_shown_falls_back_to_the_theme_background(string path)
    {
        SettingsFilesHarness harness = await SignedInAsync();
        await harness.WriteAsUserAsync("/home/alice/Pictures/fake.png", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        await harness.WriteAsUserAsync("/home/alice/Pictures/logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        await SetAsync(harness, ("wallpaper", path));
        using WallpaperService service = new(harness.Settings, harness.FileSystem, harness.Session, harness.Clock);
        await service.EnsureInitializedAsync();

        Assert.Null(await service.GetImageAsync(1));
    }

    [Fact]
    public async Task A_change_of_the_file_is_followed_and_notified()
    {
        SettingsFilesHarness harness = await SignedInAsync();
        using WallpaperService service = new(harness.Settings, harness.FileSystem, harness.Session, harness.Clock);
        await service.EnsureInitializedAsync();
        int notifications = 0;
        service.Changed += () => notifications++;

        await SetAsync(harness, ("wallpaper", "builtin:circuit"));
        await service.RefreshAsync();

        Assert.True(notifications >= 1);
        Assert.Equal("builtin:circuit", service.Current.Wallpaper);
        Assert.Equal("builtin:circuit", (await service.GetImageAsync(1))!.Value);
    }

    private static async Task<SettingsFilesHarness> SignedInAsync()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        return harness;
    }

    private static async Task SetAsync(SettingsFilesHarness harness, params (string Key, string Value)[] values) =>
        await harness.Settings.SetManyAsync(
            AppearanceSettingsDocuments.Document,
            [.. values.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value))],
            SettingLayer.User);
}
