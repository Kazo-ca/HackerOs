using System.Globalization;
using HackerOs.Simulation.Abstractions.Sounds;

namespace HackerOs.Platform.Core.Tests.Sounds;

/// <summary>ADR 0204 §1: the freedesktop.org <c>index.theme</c> of a sound theme and the sound names.</summary>
public sealed class SoundThemeIndexTests
{
    private const string Index = """
        # comment
        [Sound Theme]
        Name=Retro
        Name[fr]=Rétro
        Name[fr_CA]=Rétro (Québec)
        Comment=Old beeps
        Directories=stereo, ../escape, /abs, nested/dir, extra

        [stereo]
        Name=Ignored
        OutputProfile=stereo
        """;

    [Fact]
    public void Names_follow_the_readers_language_with_the_plain_name_as_fallback()
    {
        SoundThemeIndex theme = SoundThemeIndex.Parse("retro", Index);

        Assert.Equal("Retro", theme.Name);
        Assert.Equal("Rétro", theme.GetName(CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("Rétro (Québec)", theme.GetName(CultureInfo.GetCultureInfo("fr-CA")));
        Assert.Equal("Retro", theme.GetName(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("Old beeps", theme.GetComment(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Only_plain_sub_folders_are_searched_and_the_theme_folder_last()
    {
        SoundThemeIndex theme = SoundThemeIndex.Parse("retro", Index);

        Assert.Equal(["stereo", "extra"], theme.Directories);
        Assert.Equal(
            ["/usr/share/sounds/retro/stereo", "/usr/share/sounds/retro/extra", "/usr/share/sounds/retro"],
            theme.GetSearchDirectories("/usr/share/sounds/retro/"));
    }

    [Fact]
    public void An_empty_file_gives_a_theme_named_after_its_folder()
    {
        SoundThemeIndex theme = SoundThemeIndex.Parse("mine", null);

        Assert.Equal("mine", theme.GetName(CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Empty(theme.Directories);
        Assert.Null(theme.GetComment(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(OsSoundEvent.Notification, "message-new-instant")]
    [InlineData(OsSoundEvent.Warning, "dialog-warning")]
    [InlineData(OsSoundEvent.Error, "dialog-error")]
    [InlineData(OsSoundEvent.SignIn, "desktop-login")]
    [InlineData(OsSoundEvent.SignOut, "desktop-logout")]
    public void Every_event_has_its_freedesktop_sound_name(OsSoundEvent soundEvent, string name) =>
        Assert.Equal(name, OsSoundEvents.GetSoundName(soundEvent));

    [Theory]
    [InlineData("hackeros", true)]
    [InlineData("my-theme_2.1", true)]
    [InlineData(".hidden", false)]
    [InlineData("Upper", false)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Theme_ids_are_safe_folder_names(string? id, bool valid) =>
        Assert.Equal(valid, OsSoundEvents.IsValidThemeId(id));

    [Theory]
    [InlineData("a.oga", "audio/ogg")]
    [InlineData("a.OGG", "audio/ogg")]
    [InlineData("a.mp3", "audio/mpeg")]
    [InlineData("a.wav", null)]
    public void Only_ogg_and_mp3_are_accepted(string file, string? mediaType) =>
        Assert.Equal(mediaType, OsSoundEvents.GetMediaType(file));
}
