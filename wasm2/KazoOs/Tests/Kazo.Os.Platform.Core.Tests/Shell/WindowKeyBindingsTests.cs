using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Tests.Settings;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>ADR 0095 §3: key chords and <c>keybindings.config</c>.</summary>
public sealed class WindowKeyBindingsTests
{
    [Theory]
    [InlineData("Alt+Tab", "Alt+Tab")]
    [InlineData("alt+shift+tab", "Alt+Shift+Tab")]
    [InlineData("Shift+Alt+`", "Alt+Shift+Backquote")]
    [InlineData("Super+Up", "Super+Up")]
    [InlineData("Meta+left", "Super+Left")]
    [InlineData("Alt+f4", "Alt+F4")]
    [InlineData("Ctrl+Alt+q", "Ctrl+Alt+Q")]
    public void Chords_parse_to_a_canonical_form(string text, string canonical)
    {
        Assert.Equal(canonical, KeyChord.Parse(text).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alt+")]
    [InlineData("Alt+Alt+Q")]
    [InlineData("Hyper+Q")]
    [InlineData("Alt+F25")]
    [InlineData("Alt+NoSuchKey")]
    public void Invalid_chords_are_rejected(string text)
    {
        Assert.False(KeyChord.TryParse(text, out _));
    }

    [Fact]
    public void A_chord_matches_the_key_then_falls_back_to_the_physical_key()
    {
        KeyChord close = KeyChord.Parse("Alt+Q");
        Assert.True(close.Matches(false, true, false, false, "q", "KeyQ"));
        Assert.True(close.Matches(false, true, false, false, "œ", "KeyQ"));
        Assert.False(close.Matches(false, true, true, false, "Q", "KeyQ"));
        Assert.False(close.Matches(false, false, false, false, "q", "KeyQ"));

        // The key above Tab prints ` on QWERTY and ² on AZERTY: matched on the physical key.
        KeyChord next = KeyChord.Parse("Alt+Backquote");
        Assert.True(next.Matches(false, true, false, false, "²", "Backquote"));
        Assert.True(KeyChord.Parse("Super+Up").Matches(false, false, false, true, "ArrowUp", "ArrowUp"));
    }

    [Fact]
    public async Task A_fresh_machine_ships_a_commented_file_and_uses_the_defaults()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);

        string? content = await harness.ReadAsync("/etc/kazoos/keybindings.config");
        Assert.NotNull(content);
        Assert.Contains("#close = Alt+F4", content, StringComparison.Ordinal);
        Assert.Contains("[fallback]", content, StringComparison.Ordinal);

        IReadOnlyList<WindowKeyBinding> bindings = WindowKeyBindingsSettingsDocuments.ToBindings(
            await harness.Settings.ReadAsync(WindowKeyBindingsSettingsDocuments.Document), out IReadOnlyList<string> problems);
        Assert.Empty(problems);
        Assert.Equal(WindowKeyBindingsSettingsDocuments.DefaultBindings, bindings);
        Assert.True(OsSettingsDocumentRegistry.TryGet("keybindings", out _));
    }

    [Fact]
    public async Task A_user_line_replaces_a_shortcut_and_a_bad_chord_is_skipped_alone()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsRootAsync(
            "/home/alice/.config/kazoos/keybindings.config",
            "[fallback]\nclose = Ctrl+Alt+W, Alt+Nope\nminimize =\n");

        IReadOnlyList<WindowKeyBinding> bindings = WindowKeyBindingsSettingsDocuments.ToBindings(
            await harness.Settings.ReadAsync(WindowKeyBindingsSettingsDocuments.Document), out IReadOnlyList<string> problems);

        WindowKeyBinding close = Assert.Single(bindings, binding =>
            binding.Action == WindowKeyboardAction.Close && binding.Set == WindowKeyBindingSet.Fallback);
        Assert.Equal("Ctrl+Alt+W", close.Chord.ToString());
        Assert.DoesNotContain(bindings, binding =>
            binding.Action == WindowKeyboardAction.Minimize && binding.Set == WindowKeyBindingSet.Fallback);
        Assert.Contains("Alt+Nope", Assert.Single(problems), StringComparison.Ordinal);
    }
}
