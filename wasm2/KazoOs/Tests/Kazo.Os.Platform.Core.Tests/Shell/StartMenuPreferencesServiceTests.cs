using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Shell;

public sealed class StartMenuPreferencesServiceTests
{
    [Fact]
    public async Task InitializeAsync_starts_each_user_with_an_empty_pin_list()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, []);

        await service.InitializeAsync();

        Assert.Empty(service.GetPinnedAppIds(alice.UserId));
        Assert.Empty(service.GetPinnedAppIds(harness.Bob.Id));
    }

    [Fact]
    public async Task PinAsync_persists_deduplicates_and_reloads_unknown_ids()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, []);
        await service.InitializeAsync();

        Assert.True(await service.PinAsync(alice.UserId, "org.example.unmounted"));
        Assert.False(await service.PinAsync(alice.UserId, "org.example.unmounted"));
        Assert.Equal(["org.example.unmounted"], service.GetPinnedAppIds(alice.UserId));

        // Pins are the user's own file (ADR 0044 section 8), not a shared machine document.
        Assert.Contains("org.example.unmounted", await harness.ReadAsync("/home/alice/.config/kazoos/start-menu.json"));

        using StartMenuPreferencesService reloaded = new(harness.Settings, harness.Session, []);
        await reloaded.InitializeAsync();
        Assert.Equal(["org.example.unmounted"], reloaded.GetPinnedAppIds(alice.UserId));
    }

    [Fact]
    public async Task MoveAsync_reorders_the_session_profile_and_never_touches_another_user()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session, []);
        await service.InitializeAsync();
        await service.PinAsync(alice.UserId, "org.kazoos.browser");
        await service.PinAsync(alice.UserId, "org.kazoos.files");
        await service.PinAsync(alice.UserId, "org.kazoos.settings");

        Assert.False(await service.PinAsync(harness.Bob.Id, "org.example.private"));
        Assert.True(await service.MoveAsync(alice.UserId, "org.kazoos.settings", targetIndex: 0));

        Assert.Equal(
            ["org.kazoos.settings", "org.kazoos.browser", "org.kazoos.files"],
            service.GetPinnedAppIds(alice.UserId));
        Assert.Empty(service.GetPinnedAppIds(harness.Bob.Id));
        Assert.Null(await harness.ReadAsync("/home/bob/.config/kazoos/start-menu.json"));

        using StartMenuPreferencesService reloaded = new(harness.Settings, harness.Session, []);
        await reloaded.InitializeAsync();
        Assert.Equal(service.GetPinnedAppIds(alice.UserId), reloaded.GetPinnedAppIds(alice.UserId));
    }

    [Fact]
    public async Task A_new_profile_starts_with_the_store_pinned()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using StartMenuPreferencesService service = new(harness.Settings, harness.Session);

        await service.InitializeAsync();

        Assert.Equal(["org.kazoos.store"], service.GetPinnedAppIds(alice.UserId));
        string? file = await harness.ReadAsync("/home/alice/.config/kazoos/start-menu.json");
        Assert.Contains("\"defaultPinsApplied\":[\"org.kazoos.store\"]", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_existing_profile_gets_the_store_once_after_its_own_pins()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using (StartMenuPreferencesService before = new(harness.Settings, harness.Session, []))
        {
            await before.InitializeAsync();
            await before.PinAsync(alice.UserId, "org.kazoos.terminal");
        }

        using StartMenuPreferencesService service = new(harness.Settings, harness.Session);
        await service.InitializeAsync();

        Assert.Equal(["org.kazoos.terminal", "org.kazoos.store"], service.GetPinnedAppIds(alice.UserId));
    }

    [Fact]
    public async Task An_unpinned_store_stays_unpinned()
    {
        SettingsFilesHarness harness = new();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        using (StartMenuPreferencesService service = new(harness.Settings, harness.Session))
        {
            await service.InitializeAsync();
            Assert.True(await service.UnpinAsync(alice.UserId, "org.kazoos.store"));
        }

        using StartMenuPreferencesService reloaded = new(harness.Settings, harness.Session);
        await reloaded.InitializeAsync();

        Assert.Empty(reloaded.GetPinnedAppIds(alice.UserId));
    }
}
