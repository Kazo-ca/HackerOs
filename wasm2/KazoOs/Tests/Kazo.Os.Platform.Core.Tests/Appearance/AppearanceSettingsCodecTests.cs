using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Appearance;

public sealed class AppearanceSettingsCodecTests
{
    [Fact]
    public void TryDecode_version_one_migrates_missing_theme_choices_to_defaults()
    {
        bool decoded = AppearanceSettingsCodec.TryDecode(
            """{"schemaVersion":1,"accent":"purple","animationsEnabled":false}""",
            out ThemePreferences preferences);

        Assert.True(decoded);
        Assert.Equal(WellKnownThemeIds.HackerOs, preferences.DesktopThemeId);
        Assert.Equal(WellKnownThemeIds.Android, preferences.MobileThemeId);
        Assert.Equal(WellKnownAccentIds.Purple, preferences.AccentId);
        Assert.False(preferences.AnimationsEnabled);
    }

    [Fact]
    public void Encode_and_decode_version_two_round_trip_all_preferences()
    {
        ThemePreferences expected = new(
            WellKnownThemeIds.Windows7,
            WellKnownThemeIds.Ios,
            WellKnownAccentIds.Cyan,
            AnimationsEnabled: false);

        string encoded = AppearanceSettingsCodec.Encode(expected);

        Assert.StartsWith("{\"schemaVersion\":2", encoded, StringComparison.Ordinal);
        Assert.True(AppearanceSettingsCodec.TryDecode(encoded, out ThemePreferences actual));
        Assert.Equal(expected, actual);
    }
}
