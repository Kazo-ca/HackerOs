using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Shell;

public sealed class UiPlatformPreferenceServiceTests
{
    [Fact]
    public async Task InitializeAsync_defaults_to_auto_and_the_probes_suggestion()
    {
        FakeProbe probe = new(WellKnownAppPlatforms.Desktop);
        UiPlatformPreferenceService service = await CreateServiceAsync(probe);

        await service.InitializeAsync();

        Assert.Equal(UiPlatformSelectionSource.Auto, service.Current.SelectionSource);
        Assert.Null(service.Current.ExplicitPlatformId);
        Assert.Equal(WellKnownAppPlatforms.Desktop, service.Current.ActivePlatform);
    }

    [Fact]
    public async Task SetExplicitAsync_persists_and_updates_active_platform()
    {
        FakeProbe probe = new(WellKnownAppPlatforms.Desktop);
        SettingsFilesHarness harness = new();
        UiPlatformPreferenceService service = await CreateServiceAsync(probe, harness);
        await service.InitializeAsync();

        bool raised = false;
        service.Changed += () => raised = true;

        await service.SetExplicitAsync(WellKnownAppPlatforms.Mobile);

        Assert.True(raised);
        Assert.Equal(UiPlatformSelectionSource.Explicit, service.Current.SelectionSource);
        Assert.Equal(WellKnownAppPlatforms.Mobile, service.Current.ExplicitPlatformId);
        Assert.Equal(WellKnownAppPlatforms.Mobile, service.Current.ActivePlatform);

        // Persisted: a freshly initialized service reads the same choice back.
        UiPlatformPreferenceService reloaded = new(harness.Settings, probe);
        await reloaded.InitializeAsync();
        Assert.Equal(UiPlatformSelectionSource.Explicit, reloaded.Current.SelectionSource);
        Assert.Equal(WellKnownAppPlatforms.Mobile, reloaded.Current.ExplicitPlatformId);

        // ...because it is a real user file (ADR 0044), editable with nano.
        string? file = await harness.ReadAsync("/home/alice/.config/hackeros/ui-platform-preference.config");
        Assert.NotNull(file);
        Assert.Contains("selectionSource=explicit", file);
        Assert.Contains($"explicitPlatformId={WellKnownAppPlatforms.Mobile.Value}", file);
    }

    [Fact]
    public async Task Locked_choice_in_etc_wins_and_user_writes_keep_the_files_unchanged()
    {
        FakeProbe probe = new(WellKnownAppPlatforms.Desktop);
        SettingsFilesHarness harness = new();
        await harness.WriteAsRootAsync(
            "/etc/hackeros/ui-platform-preference.config",
            $"selectionSource=explicit\nexplicitPlatformId={WellKnownAppPlatforms.Mobile.Value}\n[locked]\nselectionSource=true\nexplicitPlatformId=true\n");
        UiPlatformPreferenceService service = await CreateServiceAsync(probe, harness);

        await service.SetExplicitAsync(WellKnownAppPlatforms.Desktop);

        Assert.Equal(WellKnownAppPlatforms.Mobile, service.Current.ActivePlatform);
        Assert.Null(await harness.ReadAsync("/home/alice/.config/hackeros/ui-platform-preference.config"));
    }

    [Fact]
    public async Task ClearToAutoAsync_reverts_to_the_probes_suggestion()
    {
        FakeProbe probe = new(WellKnownAppPlatforms.Mobile);
        UiPlatformPreferenceService service = await CreateServiceAsync(probe);
        await service.InitializeAsync();
        await service.SetExplicitAsync(WellKnownAppPlatforms.Desktop);

        await service.ClearToAutoAsync();

        Assert.Equal(UiPlatformSelectionSource.Auto, service.Current.SelectionSource);
        Assert.Null(service.Current.ExplicitPlatformId);
        Assert.Equal(WellKnownAppPlatforms.Mobile, service.Current.ActivePlatform);
    }

    [Fact]
    public async Task Probe_change_while_auto_updates_active_platform_and_raises_changed()
    {
        FakeProbe probe = new(WellKnownAppPlatforms.Desktop);
        UiPlatformPreferenceService service = await CreateServiceAsync(probe);
        await service.InitializeAsync();

        bool raised = false;
        service.Changed += () => raised = true;

        probe.SetSuggestion(WellKnownAppPlatforms.Mobile);

        Assert.True(raised);
        Assert.Equal(WellKnownAppPlatforms.Mobile, service.Current.ActivePlatform);
    }

    [Fact]
    public async Task Probe_change_while_explicit_does_not_change_active_platform()
    {
        FakeProbe probe = new(WellKnownAppPlatforms.Desktop);
        UiPlatformPreferenceService service = await CreateServiceAsync(probe);
        await service.InitializeAsync();
        await service.SetExplicitAsync(WellKnownAppPlatforms.Desktop);

        probe.SetSuggestion(WellKnownAppPlatforms.Mobile);

        Assert.Equal(WellKnownAppPlatforms.Desktop, service.Current.ActivePlatform);
    }

    private static async Task<UiPlatformPreferenceService> CreateServiceAsync(FakeProbe probe, SettingsFilesHarness? harness = null)
    {
        harness ??= new SettingsFilesHarness();
        await harness.LoginAsync(harness.Alice);
        return new UiPlatformPreferenceService(harness.Settings, probe);
    }

    private sealed class FakeProbe(AppPlatformId initialSuggestion) : IPlatformEnvironmentProbe
    {
        public PlatformEnvironmentSnapshot Current { get; private set; } = new(
            new PlatformEnvironmentSignals(1280, 800, false, true, 0, false),
            initialSuggestion,
            "test");

        public event Action? Changed;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void SetSuggestion(AppPlatformId suggestion)
        {
            Current = Current with { SuggestedPlatform = suggestion };
            Changed?.Invoke();
        }
    }
}
