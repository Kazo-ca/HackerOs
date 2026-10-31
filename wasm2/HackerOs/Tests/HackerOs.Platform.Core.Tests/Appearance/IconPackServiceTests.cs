using HackerOs.Platform.Core.Appearance;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Theming.Abstractions.IconPacks;

namespace HackerOs.Platform.Core.Tests.Appearance;

/// <summary>The shell's selected icon pack and the shipped Néon pack (ADR 0203 §3, §4 and §6).</summary>
public sealed class IconPackServiceTests
{
    [Fact]
    public async Task The_seeder_installs_the_shipped_packs_in_usr_share_icons()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);

        Assert.NotEmpty(BuiltInIconPacks.Files);
        string? manifest = await harness.ReadAsync("/usr/share/icons/neon/icon-pack.json");
        Assert.Contains("\"id\": \"neon\"", manifest, StringComparison.Ordinal);
        Assert.NotNull(await harness.StatAsync("/usr/share/icons/neon/files/folder.png"));
        Assert.NotNull(await harness.StatAsync("/usr/share/icons/neon/apps/org.hackeros.terminal.png"));
    }

    [Fact]
    public async Task The_built_in_icons_apply_until_a_pack_is_chosen_then_the_pack_applies_live()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        using IconPackService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);
        await service.InitializeAsync();
        Assert.Equal(IconPackLibrary.BuiltInId, service.SelectedId);
        Assert.Null(service.Current);
        Assert.Null(service.FindAppImage("org.hackeros.terminal", "utilities", hasDeclaredImage: true));
        int notifications = 0;
        service.Changed += () => notifications++;

        await harness.Settings.SetAsync(IconPackSettingsDocuments.Document, IconPackLibrary.PackIdKey, "neon", SettingLayer.User);
        await service.RefreshAsync();

        Assert.Equal("neon", service.Current?.Info.Id);
        Assert.Equal(1, notifications);
        Assert.StartsWith("data:image/png;base64,", service.FindAppImage("org.hackeros.terminal", "utilities", hasDeclaredImage: true), StringComparison.Ordinal);
        // A category image replaces only the generic icon of an app that declares none.
        Assert.Null(service.FindAppImage("org.example.unknown", "utilities", hasDeclaredImage: true));
        Assert.NotNull(service.FindAppImage("org.example.unknown", "utilities", hasDeclaredImage: false));
    }

    [Fact]
    public async Task An_unknown_pack_id_keeps_the_built_in_icons()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.Settings.SetAsync(IconPackSettingsDocuments.Document, IconPackLibrary.PackIdKey, "missing", SettingLayer.User);
        using IconPackService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);

        await service.InitializeAsync();

        Assert.Equal("missing", service.SelectedId);
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task A_users_own_pack_in_local_share_icons_is_read_with_their_rights()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.local/share/icons/mine/icon-pack.json", """{"id":"mine","name":"Mine"}""");
        await harness.Settings.SetAsync(IconPackSettingsDocuments.Document, IconPackLibrary.PackIdKey, "mine", SettingLayer.User);
        using IconPackService service = new(harness.FileSystem, harness.Session, harness.Clock, harness.Settings);

        await service.InitializeAsync();

        Assert.Equal("/home/alice/.local/share/icons/mine", service.Current?.Info.RootPath);
        Assert.Equal(IconPackOrigin.User, service.Current?.Info.Origin);
    }
}
