using Kazo.Os.Platform.Core.Clipboard;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Tests.Settings;

namespace Kazo.Os.Platform.Core.Tests.Clipboard;

/// <summary>The <c>sharing</c> key of <c>/etc/hackeros/clipboard.config</c> (ADR 0065, ADR 0044).</summary>
public sealed class ClipboardSettingsDocumentsTests
{
    [Theory]
    [InlineData("bidirectional", ClipboardSharingMode.Bidirectional, true, true)]
    [InlineData("guest-to-host", ClipboardSharingMode.GuestToHost, true, false)]
    [InlineData("host-to-guest", ClipboardSharingMode.HostToGuest, false, true)]
    [InlineData("disabled", ClipboardSharingMode.Disabled, false, false)]
    [InlineData("sideways", ClipboardSharingMode.Bidirectional, true, true)]
    public void Each_value_maps_to_a_mode_and_its_directions(string value, ClipboardSharingMode mode, bool toHost, bool toGuest)
    {
        Assert.Equal(mode, ClipboardSettingsDocuments.ParseMode(value));
        Assert.Equal(toHost, mode.AllowsGuestToHost());
        Assert.Equal(toGuest, mode.AllowsHostToGuest());
    }

    [Fact]
    public async Task A_fresh_machine_ships_a_commented_system_file_defaulting_to_bidirectional()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);

        string? content = await harness.ReadAsync("/etc/hackeros/clipboard.config");
        Assert.NotNull(content);
        Assert.Contains("sharing", content);
        Assert.Equal(
            ClipboardSharingMode.Bidirectional,
            ClipboardSettingsDocuments.ToMode(await harness.Settings.ReadAsync(ClipboardSettingsDocuments.Document)));
        Assert.True(OsSettingsDocumentRegistry.TryGet("clipboard", out _));
    }

    [Fact]
    public async Task A_user_overrides_the_machine_default_unless_the_administrator_locked_it()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsRootAsync("/home/alice/.config/hackeros/clipboard.config", "sharing=disabled\n");

        Assert.Equal(
            ClipboardSharingMode.Disabled,
            ClipboardSettingsDocuments.ToMode(await harness.Settings.ReadAsync(ClipboardSettingsDocuments.Document)));

        await harness.WriteAsRootAsync("/etc/hackeros/clipboard.config", "sharing=guest-to-host\n[locked]\nsharing=true\n");

        Assert.Equal(
            ClipboardSharingMode.GuestToHost,
            ClipboardSettingsDocuments.ToMode(await harness.Settings.ReadAsync(ClipboardSettingsDocuments.Document)));
    }
}
