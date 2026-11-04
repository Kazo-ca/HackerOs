using Kazo.Os.Platform.Core.Appearance;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Theming.Abstractions;

namespace Kazo.Os.Platform.Core.Tests.Settings;

/// <summary>One-time copy of the retired IndexedDB settings store into files (ADR 0044 section 9).</summary>
public sealed class SettingsStoreToFilesMigrationTests
{
    private const string EditorAppId = "org.example.editor";

    [Fact]
    public async Task Fresh_install_gets_commented_defaults_in_etc_and_a_marker()
    {
        SettingsFilesHarness harness = new();

        await harness.BootAsync();

        string? appearance = await harness.ReadAsync("/etc/kazoos/appearance.config");
        Assert.NotNull(appearance);
        Assert.StartsWith("# Kazo OS default for every user.", appearance);
        Assert.Equal(FileAssociationSettingsDocuments.DefaultContent, await harness.ReadAsync("/etc/kazoos/file-associations.json"));
        Assert.NotNull(await harness.StatAsync(SettingsStoreToFilesMigration.SystemMarkerPath));
        Assert.Equal(0x01A4, (await harness.StatAsync("/etc/kazoos/policy.config"))!.Permissions.Mode);
    }

    [Fact]
    public async Task Legacy_os_documents_become_etc_files_once_and_admin_edits_survive_reboots()
    {
        ThemePreferences legacy = new(WellKnownThemeIds.Ubuntu, WellKnownThemeIds.Ios, WellKnownAccentIds.Purple, AnimationsEnabled: false);
        FakeLegacyStore store = new(
            new LegacySettingsRecord(SettingsScope.OsAdmin, null, null, "appearance", AppearanceSettingsCodec.Encode(legacy)),
            new LegacySettingsRecord(SettingsScope.OsAdmin, null, null, "policy", "capabilityGrantsLocked=true\n"),
            new LegacySettingsRecord(SettingsScope.OsAdmin, null, null, "file-associations", "not json"));
        SettingsFilesHarness harness = new(store);

        await harness.BootAsync();

        LayeredConfigSnapshot appearance = await harness.Settings.ReadAsync(AppearanceSettingsDocuments.Document);
        Assert.Equal(legacy, AppearanceSettingsDocuments.ToPreferences(appearance));
        Assert.Equal("capabilityGrantsLocked=true\n", await harness.ReadAsync("/etc/kazoos/policy.config"));
        // An invalid legacy document is not copied: the installation default is written instead.
        Assert.Equal(FileAssociationSettingsDocuments.DefaultContent, await harness.ReadAsync("/etc/kazoos/file-associations.json"));

        await harness.WriteAsRootAsync("/etc/kazoos/policy.config", "capabilityGrantsLocked=false\n");
        await harness.BootAsync();

        Assert.Equal("capabilityGrantsLocked=false\n", await harness.ReadAsync("/etc/kazoos/policy.config"));
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Login_copies_only_that_users_app_settings_pins_and_geometry()
    {
        FakeLegacyStore store = new();
        SettingsFilesHarness harness = new(store);
        string aliceId = harness.Alice.Id.ToString();
        string bobId = harness.Bob.Id.ToString();
        string startMenu = "{\"schemaVersion\":1,\"profiles\":{"
            + $"\"{aliceId}\":{{\"pinnedAppIds\":[\"org.kazoos.files\"]}},"
            + $"\"{bobId}\":{{\"pinnedAppIds\":[\"org.kazoos.terminal\"]}}"
            + "}}";
        store.Records =
        [
            new LegacySettingsRecord(SettingsScope.AppUser, EditorAppId, aliceId, "settings", "theme=light\n"),
            new LegacySettingsRecord(SettingsScope.AppUser, EditorAppId, bobId, "settings", "theme=dark\n"),
            new LegacySettingsRecord(SettingsScope.AppUserDevice, EditorAppId, aliceId, "window-geometry",
                """{"hasValue":true,"x":10,"y":20,"width":640.5,"height":480}"""),
            new LegacySettingsRecord(SettingsScope.OsAdmin, null, null, "start-menu", startMenu)
        ];

        await harness.LoginAsync(harness.Alice);

        Assert.Equal("theme=light\n", await harness.ReadAsync($"/home/alice/.config/{EditorAppId}/settings.config"));
        Assert.Equal("x=10\ny=20\nwidth=640.5\nheight=480\n", await harness.ReadAsync($"/home/alice/.local/state/kazoos/windows/{EditorAppId}.config"));
        string? pins = await harness.ReadAsync("/home/alice/.config/kazoos/start-menu.json");
        Assert.NotNull(pins);
        Assert.Contains("org.kazoos.files", pins);
        Assert.DoesNotContain("org.kazoos.terminal", pins);
        Assert.Null(await harness.StatAsync($"/home/bob/.config/{EditorAppId}/settings.config"));
        // Owned by alice (the filesystem records the owner by user id), so her own mode bits apply.
        Assert.Contains(
            (await harness.StatAsync($"/home/alice/.config/{EditorAppId}/settings.config"))!.OwnerId,
            new[] { "alice", aliceId });
    }

    [Fact]
    public async Task Second_login_never_overwrites_what_the_user_changed()
    {
        FakeLegacyStore store = new();
        SettingsFilesHarness harness = new(store);
        store.Records = [new LegacySettingsRecord(SettingsScope.AppUser, EditorAppId, harness.Alice.Id.ToString(), "settings", "theme=light\n")];
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync($"/home/alice/.config/{EditorAppId}/settings.config", "theme=dark\n");
        await harness.Session.LogoutAsync();

        await harness.LoginAsync(harness.Alice);

        Assert.Equal("theme=dark\n", await harness.ReadAsync($"/home/alice/.config/{EditorAppId}/settings.config"));
        Assert.NotNull(await harness.StatAsync($"/home/alice/{SettingsStoreToFilesMigration.UserMarkerRelativePath}"));
    }

    [Fact]
    public async Task Unreadable_store_never_blocks_boot_or_login()
    {
        SettingsFilesHarness harness = new(new ThrowingLegacyStore());

        await harness.BootAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);

        Assert.Equal(harness.Alice.Id, alice.UserId);
        Assert.NotNull(await harness.ReadAsync("/etc/kazoos/appearance.config"));
    }

    private sealed class FakeLegacyStore(params LegacySettingsRecord[] records) : ILegacySettingsStore
    {
        public LegacySettingsRecord[] Records { get; set; } = records;

        public int Reads { get; private set; }

        public ValueTask<IReadOnlyList<LegacySettingsRecord>> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return ValueTask.FromResult<IReadOnlyList<LegacySettingsRecord>>(Records);
        }
    }

    private sealed class ThrowingLegacyStore : ILegacySettingsStore
    {
        public ValueTask<IReadOnlyList<LegacySettingsRecord>> ReadAllAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("IndexedDB is unavailable.");
    }
}
