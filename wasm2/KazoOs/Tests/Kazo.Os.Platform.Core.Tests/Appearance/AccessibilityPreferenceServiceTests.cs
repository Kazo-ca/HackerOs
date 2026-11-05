using Kazo.Os.AppSdk.Accessibility;
using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Appearance;

/// <summary>ADR 0105: accessibility.config, its resolution against the host, and the service the shell reads.</summary>
public sealed class AccessibilityPreferenceServiceTests
{
    [Theory]
    [InlineData(AccessibilityMode.Auto, false, false, false)]
    [InlineData(AccessibilityMode.Auto, true, false, true)]
    [InlineData(AccessibilityMode.Auto, false, true, true)]
    [InlineData(AccessibilityMode.On, false, false, true)]
    [InlineData(AccessibilityMode.Off, true, true, false)]
    public void High_contrast_auto_follows_prefers_contrast_or_forced_colors(
        AccessibilityMode mode, bool moreContrast, bool forcedColors, bool expected)
    {
        KazoAccessibilityPreferences effective = AccessibilitySettingsDocuments.Resolve(
            AccessibilitySettings.Default with { HighContrast = mode },
            new HostAccessibilitySignals(moreContrast, forcedColors, PrefersReducedMotion: false),
            animationsEnabled: true);

        Assert.Equal(expected, effective.HighContrast);
    }

    [Theory]
    [InlineData(AccessibilityMode.Auto, false, true, false)]
    [InlineData(AccessibilityMode.Auto, true, true, true)]
    [InlineData(AccessibilityMode.On, false, true, true)]
    [InlineData(AccessibilityMode.Off, true, true, false)]
    // animationsEnabled = false of appearance.config counts as reducedMotion = on, even over "off".
    [InlineData(AccessibilityMode.Off, false, false, true)]
    [InlineData(AccessibilityMode.Auto, false, false, true)]
    public void Reduced_motion_follows_the_host_and_animations_enabled(
        AccessibilityMode mode, bool hostReduces, bool animationsEnabled, bool expected)
    {
        KazoAccessibilityPreferences effective = AccessibilitySettingsDocuments.Resolve(
            AccessibilitySettings.Default with { ReducedMotion = mode },
            new HostAccessibilitySignals(false, false, hostReduces),
            animationsEnabled);

        Assert.Equal(expected, effective.ReducedMotion);
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(1.25, "1.25")]
    [InlineData(1.5, "1.5")]
    [InlineData(2.0, "2.0")]
    public void Text_scales_round_trip_through_their_file_spelling(double scale, string spelling)
    {
        Assert.Equal(spelling, AccessibilitySettingsDocuments.FormatTextScale(scale));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessibilitySettingsDocuments.FormatTextScale(1.1));
    }

    [Fact]
    public void Accessibility_config_is_an_os_document_apps_may_open()
    {
        Assert.True(OsSettingsDocumentRegistry.TryGet("accessibility", out OsSettingsDocument document));
        Assert.Equal("/etc/kazoos/accessibility.config", document.SystemPath.Value);
    }

    [Fact]
    public async Task A_fresh_machine_ships_a_commented_etc_file_and_everything_follows_the_host()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();

        string? system = await harness.ReadAsync("/etc/kazoos/accessibility.config");
        Assert.NotNull(system);
        Assert.Contains("highContrast", system, StringComparison.Ordinal);
        Assert.Contains("textScale", system, StringComparison.Ordinal);
        Assert.Contains("reducedMotion", system, StringComparison.Ordinal);

        FakeHostProbe host = new(new HostAccessibilitySignals(PrefersMoreContrast: true, ForcedColors: false, PrefersReducedMotion: true));
        using AccessibilityPreferenceService service = CreateService(harness, host, out _);
        await service.InitializeAsync();

        Assert.Equal(AccessibilitySettings.Default, service.Settings);
        Assert.Equal(new KazoAccessibilityPreferences(HighContrast: true, TextScale: 1.0, ReducedMotion: true), service.Effective);
    }

    [Fact]
    public async Task The_user_file_overrides_the_machine_default_and_a_reload_notifies()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/kazoos/accessibility.config", "textScale=1.25\n");
        await harness.LoginAsync(harness.Alice);
        using AccessibilityPreferenceService service = CreateService(harness, new FakeHostProbe(HostAccessibilitySignals.None), out _);
        await service.InitializeAsync();
        Assert.Equal(1.25, service.Effective.TextScale);
        Assert.Equal(SettingLayer.System, service.Snapshot!.Sources[AccessibilitySettingsDocuments.TextScaleKey]);

        int notifications = 0;
        service.Changed += () => notifications++;
        await harness.WriteAsRootAsync("/home/alice/.config/kazoos/accessibility.config", "highContrast=on\ntextScale=2.0\n");
        await service.RefreshAsync();

        Assert.Equal(new KazoAccessibilityPreferences(HighContrast: true, TextScale: 2.0, ReducedMotion: false), service.Effective);
        Assert.Equal(SettingLayer.User, service.Snapshot!.Sources[AccessibilitySettingsDocuments.TextScaleKey]);
        Assert.True(notifications >= 1);
    }

    [Fact]
    public async Task An_invalid_value_falls_back_to_the_next_layer()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsRootAsync("/home/alice/.config/kazoos/accessibility.config", "textScale=3.0\nhighContrast=maybe\n");
        using AccessibilityPreferenceService service = CreateService(harness, new FakeHostProbe(HostAccessibilitySignals.None), out _);
        await service.InitializeAsync();

        Assert.Equal(AccessibilitySettings.Default, service.Settings);
    }

    [Fact]
    public async Task The_host_changing_its_settings_moves_auto_values_and_notifies()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        FakeHostProbe host = new(HostAccessibilitySignals.None);
        using AccessibilityPreferenceService service = CreateService(harness, host, out _);
        await service.InitializeAsync();
        Assert.False(service.Effective.ReducedMotion);

        int notifications = 0;
        service.Changed += () => notifications++;
        host.Raise(new HostAccessibilitySignals(false, false, PrefersReducedMotion: true));

        Assert.True(service.Effective.ReducedMotion);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task Turning_animations_off_in_appearance_config_reduces_motion()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        using AccessibilityPreferenceService service = CreateService(harness, new FakeHostProbe(HostAccessibilitySignals.None), out ThemePreferenceService appearance);
        await appearance.InitializeAsync();
        await service.InitializeAsync();
        Assert.False(service.Effective.ReducedMotion);

        await appearance.SetAsync(appearance.Current with { AnimationsEnabled = false });

        Assert.True(service.Effective.ReducedMotion);
        appearance.Dispose();
    }

    private static AccessibilityPreferenceService CreateService(
        SettingsFilesHarness harness, IHostAccessibilityProbe host, out ThemePreferenceService appearance)
    {
        appearance = new ThemePreferenceService(harness.Settings);
        return new AccessibilityPreferenceService(harness.Settings, appearance, host);
    }

    private sealed class FakeHostProbe(HostAccessibilitySignals initial) : IHostAccessibilityProbe
    {
        public HostAccessibilitySignals Current { get; private set; } = initial;

        public event Action? Changed;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Raise(HostAccessibilitySignals next)
        {
            Current = next;
            Changed?.Invoke();
        }
    }
}
