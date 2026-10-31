using System.Text;
using HackerOs.App.Abstractions;
using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Sounds;
using HackerOs.Platform.Core.Tests.Settings;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Notifications;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Sounds;

namespace HackerOs.Platform.Core.Tests.Sounds;

/// <summary>ADR 0204: the built-in sound themes, sound.config and the sounds of the OS events.</summary>
public sealed class OsSoundServiceTests
{
    [Fact]
    public void Sound_config_is_an_os_document_apps_may_open()
    {
        Assert.True(OsSettingsDocumentRegistry.TryGet("sound", out OsSettingsDocument document));
        Assert.Equal("/etc/hackeros/sound.config", document.SystemPath.Value);
    }

    [Fact]
    public async Task A_fresh_machine_ships_an_etc_file_with_sounds_on()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();

        string? system = await harness.ReadAsync("/etc/hackeros/sound.config");
        Assert.NotNull(system);
        Assert.Contains("theme", system, StringComparison.Ordinal);

        using OsSoundService service = Create(harness, new RecordingOutput());
        await service.InitializeAsync();
        Assert.Equal(SoundPreferences.Default, service.Preferences);
    }

    [Fact]
    public async Task The_first_sign_in_installs_the_built_in_themes_as_real_ogg_files_after_the_login()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        using OsSoundService service = Create(harness, new RecordingOutput());
        await harness.LoginAsync(harness.Alice);
        await service.InstallBuiltInThemesAsync();

        foreach (BuiltInSoundTheme theme in BuiltInSoundThemes.All)
        {
            string? index = await harness.ReadAsync($"/usr/share/sounds/{theme.Id}/index.theme");
            Assert.Contains($"Name={theme.Name}", index, StringComparison.Ordinal);
            foreach (OsSoundEvent soundEvent in OsSoundEvents.All)
            {
                FileSystemEntryMetadata? file = await harness.StatAsync(BuiltInSoundThemes.GetSoundPath(theme.Id, soundEvent));
                FileMetadata metadata = Assert.IsType<FileMetadata>(file);
                Assert.InRange(metadata.Length, 1000, OsSoundEvents.MaxSoundFileBytes);
                Assert.Equal("OggS", Encoding.ASCII.GetString(BuiltInSoundThemes.ReadSound(theme.Id, soundEvent)!, 0, 4));
            }
        }

        Assert.NotNull(await harness.StatAsync("/home/alice/.local/share/sounds"));
    }

    [Fact]
    public async Task Signing_in_plays_the_login_sound_of_the_default_theme_at_the_default_volume()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        RecordingOutput output = new();
        using OsSoundService service = Create(harness, output);
        await service.InitializeAsync();

        await harness.LoginAsync(harness.Alice);

        (OsSoundClip clip, double volume) = await output.NextAsync();
        Assert.StartsWith("/usr/share/sounds/hackeros/stereo/desktop-login.oga#", clip.Key, StringComparison.Ordinal);
        Assert.Equal("audio/ogg", clip.MediaType);
        Assert.Equal(0.6, volume, 3);
        Assert.Equal("OggS", Encoding.ASCII.GetString(await clip.LoadAsync(CancellationToken.None), 0, 4));
    }

    [Fact]
    public async Task Turned_off_sounds_play_nothing()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/hackeros/sound.config", "enabled=false\n");
        await harness.LoginAsync(harness.Alice);
        RecordingOutput output = new();
        using OsSoundService service = Create(harness, output);
        await service.InitializeAsync();

        Assert.False(service.Preferences.Enabled);
        Assert.False(await service.PlayAsync(OsSoundEvent.Error));
        Assert.Empty(output.Played);
    }

    [Theory]
    [InlineData(NotificationSeverity.Information, "message-new-instant")]
    [InlineData(NotificationSeverity.Success, "message-new-instant")]
    [InlineData(NotificationSeverity.Warning, "dialog-warning")]
    [InlineData(NotificationSeverity.Error, "dialog-error")]
    public async Task A_notification_of_the_session_user_plays_the_sound_of_its_severity(NotificationSeverity severity, string sound)
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        InMemoryNotificationQueue queue = new(10);
        RecordingOutput output = new();
        using OsSoundService service = Create(harness, output, queue);
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();

        queue.Enqueue(Notify(harness.Bob.Id, severity));
        queue.Enqueue(Notify(harness.Alice.Id, severity));

        (OsSoundClip clip, _) = await output.NextAsync();
        Assert.Contains($"/stereo/{sound}.oga#", clip.Key, StringComparison.Ordinal);
        await Task.Delay(50);
        Assert.Single(output.Played);
    }

    [Fact]
    public async Task Do_not_disturb_mutes_the_notification_sounds_only()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        InMemoryNotificationQueue queue = new(10);
        RecordingOutput output = new();
        using DoNotDisturbService doNotDisturb = new(harness.Settings);
        await doNotDisturb.InitializeAsync();
        await doNotDisturb.SetAsync(true);
        using OsSoundService service = new(
            harness.Settings, harness.FileSystem, harness.Clock, harness.Session, output, harness.EventBus, queue,
            installBuiltInThemes: harness.Seeder.InstallSoundThemesAsync)
        {
            DoNotDisturb = doNotDisturb,
        };
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();

        queue.Enqueue(Notify(harness.Alice.Id, NotificationSeverity.Error));
        await Task.Delay(100);
        Assert.Empty(output.Played);

        // ADR 0216 §1: the other events still sound.
        Assert.True(await service.PlayAsync(OsSoundEvent.Warning));
    }

    [Fact]
    public async Task A_user_theme_plays_its_own_files_and_falls_back_to_the_default_theme()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.local/share/sounds/retro/index.theme", "[Sound Theme]\nName=Retro\n");
        await harness.WriteAsUserAsync("/home/alice/.local/share/sounds/retro/dialog-error.mp3", "ID3 fake");
        await harness.WriteAsUserAsync("/home/alice/.config/hackeros/sound.config", "theme=retro\nvolume=100\n");
        RecordingOutput output = new();
        using OsSoundService service = Create(harness, output);
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();

        Assert.True(await service.PlayAsync(OsSoundEvent.Error));
        Assert.True(await service.PlayAsync(OsSoundEvent.Notification));

        Assert.StartsWith("/home/alice/.local/share/sounds/retro/dialog-error.mp3#", output.Played[0].Clip.Key, StringComparison.Ordinal);
        Assert.Equal("audio/mpeg", output.Played[0].Clip.MediaType);
        Assert.Equal(1.0, output.Played[0].Volume, 3);
        Assert.StartsWith("/usr/share/sounds/hackeros/", output.Played[1].Clip.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_theme_pack_sound_theme_is_found_by_the_pack_id()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        const string pack = "/home/alice/.local/share/hackeros/theme-packs/org.example.neon/sounds";
        await harness.WriteAsUserAsync($"{pack}/index.theme", "[Sound Theme]\nName=Neon\n");
        await harness.WriteAsUserAsync($"{pack}/dialog-error.ogg", "OggS fake");
        using OsSoundService service = Create(harness, new RecordingOutput());
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();

        OsSoundClip? clip = await service.ResolveAsync("org.example.neon", OsSoundEvent.Error);

        Assert.StartsWith($"{pack}/dialog-error.ogg#", clip!.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sound_over_one_megabyte_is_not_played()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.local/share/sounds/big/index.theme", "[Sound Theme]\nName=Big\n");
        await harness.WriteAsUserAsync("/home/alice/.local/share/sounds/big/dialog-error.ogg", new string('x', (int)OsSoundEvents.MaxSoundFileBytes + 1));
        RecordingOutput output = new();
        using OsSoundService service = Create(harness, output);
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();

        OsSoundClip? clip = await service.ResolveAsync("big", OsSoundEvent.Error);

        Assert.StartsWith("/usr/share/sounds/hackeros/", clip!.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_user_folder_cannot_hide_a_system_theme()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsUserAsync("/home/alice/.local/share/sounds/soft/index.theme", "[Sound Theme]\nName=Fake\n");
        await harness.WriteAsUserAsync("/home/alice/.local/share/sounds/soft/dialog-error.ogg", "OggS fake");
        using OsSoundService service = Create(harness, new RecordingOutput());
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();

        OsSoundClip? clip = await service.ResolveAsync("soft", OsSoundEvent.Error);

        Assert.StartsWith("/usr/share/sounds/soft/stereo/dialog-error.oga#", clip!.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Choosing_a_theme_in_the_session_notifies_and_plays_its_notification_sound()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        RecordingOutput output = new();
        using OsSoundService service = Create(harness, output);
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => changed.TrySetResult();

        await harness.Settings.SetAsync(SoundSettingsDocuments.Document, "theme", "soft");

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("soft", service.Preferences.Theme);
        (OsSoundClip clip, _) = await output.NextAsync();
        Assert.StartsWith("/usr/share/sounds/soft/stereo/message-new-instant.oga#", clip.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_event_twice_at_once_plays_once()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        RecordingOutput output = new();
        using OsSoundService service = Create(harness, output);
        await service.InstallBuiltInThemesAsync();
        await service.InitializeAsync();

        Assert.True(await service.PlayAsync(OsSoundEvent.Warning));
        Assert.False(await service.PlayAsync(OsSoundEvent.Warning));
        Assert.True(await service.PlayAsync(OsSoundEvent.Error));
    }

    [Theory]
    [InlineData("volume=250\n", 100)]
    [InlineData("volume=-3\n", 0)]
    [InlineData("volume=loud\n", SoundSettingsDocuments.DefaultVolume)]
    public async Task The_volume_is_clamped_and_an_invalid_value_is_the_default(string content, int expected)
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/hackeros/sound.config", content);
        using OsSoundService service = Create(harness, new RecordingOutput());

        await service.InitializeAsync();

        Assert.Equal(expected, service.Preferences.Volume);
    }

    private static OsSoundService Create(SettingsFilesHarness harness, IOsSoundOutput output, INotificationFeed? feed = null) =>
        new(
            harness.Settings, harness.FileSystem, harness.Clock, harness.Session, output, harness.EventBus, feed,
            installBuiltInThemes: harness.Seeder.InstallSoundThemesAsync);

    private static Notification Notify(Simulation.Abstractions.Sessions.LocalUserId user, NotificationSeverity severity) =>
        new(
            NotificationId.FromGuid(Guid.NewGuid()),
            user,
            "org.hackeros.test",
            severity,
            "Title",
            "Message",
            [],
            DateTimeOffset.UnixEpoch,
            null);

    /// <summary>A sound card that records what it was asked to play.</summary>
    private sealed class RecordingOutput : IOsSoundOutput
    {
        private readonly SemaphoreSlim _played = new(0);

        internal List<(OsSoundClip Clip, double Volume)> Played { get; } = [];

        public ValueTask<bool> PlayAsync(OsSoundClip clip, double volume, CancellationToken cancellationToken = default)
        {
            lock (Played)
            {
                Played.Add((clip, volume));
            }

            _played.Release();
            return ValueTask.FromResult(true);
        }

        internal async Task<(OsSoundClip Clip, double Volume)> NextAsync()
        {
            Assert.True(await _played.WaitAsync(TimeSpan.FromSeconds(5)), "No sound was played.");
            lock (Played)
            {
                return Played[^1];
            }
        }
    }
}
