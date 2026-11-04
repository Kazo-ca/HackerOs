using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Appearance;

public sealed class ThemePreferenceServiceTests
{
    [Fact]
    public async Task RefreshAsync_reloads_an_external_edit_of_the_user_file_and_notifies()
    {
        SettingsFilesHarness harness = new();
        await harness.LoginAsync(harness.Alice);
        using ThemePreferenceService service = new(harness.Settings);
        await service.InitializeAsync();
        Assert.Equal(ThemePreferences.Default, service.Current);

        int notifications = 0;
        service.Changed += () => notifications++;
        await harness.WriteAsRootAsync(
            "/home/alice/.config/hackeros/appearance.config",
            $"desktopThemeId={WellKnownThemeIds.Ubuntu}\nmobileThemeId={WellKnownThemeIds.Ios}\naccent={WellKnownAccentIds.Purple}\nanimationsEnabled=false\n");

        await service.RefreshAsync();

        Assert.Equal(
            new ThemePreferences(WellKnownThemeIds.Ubuntu, WellKnownThemeIds.Ios, WellKnownAccentIds.Purple, AnimationsEnabled: false),
            service.Current);
        Assert.True(notifications >= 1);
    }

    [Fact]
    public async Task SetAsync_writes_only_changed_keys_to_the_user_layer()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        using ThemePreferenceService service = new(harness.Settings);
        await service.InitializeAsync();

        await service.SetAsync(ThemePreferences.Default with { AnimationsEnabled = false });

        string? user = await harness.ReadAsync("/home/alice/.config/hackeros/appearance.config");
        Assert.Equal("animationsEnabled=false\n", user);
        Assert.Equal(SettingLayer.User, service.Snapshot!.Sources[AppearanceSettingsDocuments.AnimationsKey]);
        // The installation ships a real /etc file listing every default, so untouched keys come from it.
        Assert.Equal(SettingLayer.System, service.Snapshot.Sources[AppearanceSettingsDocuments.DesktopThemeKey]);
        Assert.False(service.Current.AnimationsEnabled);
    }

    [Fact]
    public async Task Administrator_default_applies_to_every_user_until_they_override_it()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync(
            "/etc/hackeros/appearance.config",
            $"desktopThemeId={WellKnownThemeIds.Ubuntu}\n");
        await harness.LoginAsync(harness.Alice);
        using ThemePreferenceService service = new(harness.Settings);
        await service.InitializeAsync();

        Assert.Equal(WellKnownThemeIds.Ubuntu, service.Current.DesktopThemeId);
        Assert.Equal(SettingLayer.System, service.Snapshot!.Sources[AppearanceSettingsDocuments.DesktopThemeKey]);
    }

    [Fact]
    public async Task Locked_key_ignores_the_user_layer_and_rejects_user_writes()
    {
        SettingsFilesHarness harness = new();
        await harness.WriteAsRootAsync(
            "/etc/hackeros/appearance.config",
            "animationsEnabled=false\n[locked]\nanimationsEnabled=true\n");
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsRootAsync("/home/alice/.config/hackeros/appearance.config", "animationsEnabled=true\n");
        using ThemePreferenceService service = new(harness.Settings);
        await service.InitializeAsync();

        Assert.False(service.Current.AnimationsEnabled);
        Assert.Contains(AppearanceSettingsDocuments.AnimationsKey, service.Snapshot!.LockedKeys);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetAsync(service.Current with { AnimationsEnabled = true }));
    }

    [Fact]
    public async Task System_write_by_an_ordinary_user_is_denied_by_the_file_permissions()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        var alice = await harness.LoginAsync(harness.Alice);
        using ThemePreferenceService service = new(harness.Settings);
        await service.InitializeAsync();
        var settingsApp = harness.CreateAppGateway(
            alice,
            "org.hackeros.settings",
            Kazo.Os.App.Abstractions.AppCapabilities.FileSystemSystemRead,
            Kazo.Os.App.Abstractions.AppCapabilities.FileSystemSystemWrite);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetAsync(
            ThemePreferences.Default with { AnimationsEnabled = false }, SettingLayer.System, settingsApp));
        Assert.True(service.Current.AnimationsEnabled);
    }
}
