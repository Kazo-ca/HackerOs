using HackerOs.Platform.Core.Notifications;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Tests.Settings;

namespace HackerOs.Platform.Core.Tests.Notifications;

/// <summary>ADR 0216: the "Do not disturb" mode of notifications.config.</summary>
public sealed class DoNotDisturbServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public void Notifications_config_is_an_os_document_apps_may_open()
    {
        Assert.True(OsSettingsDocumentRegistry.TryGet("notifications", out OsSettingsDocument document));
        Assert.Equal("/etc/hackeros/notifications.config", document.SystemPath.Value);
    }

    [Fact]
    public async Task A_fresh_machine_ships_an_etc_file_with_the_mode_off()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();

        string? system = await harness.ReadAsync("/etc/hackeros/notifications.config");
        Assert.NotNull(system);
        Assert.Contains("doNotDisturb", system, StringComparison.Ordinal);

        await harness.LoginAsync(harness.Alice);
        using DoNotDisturbService service = new(harness.Settings);
        await service.InitializeAsync();
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public async Task Turning_it_on_writes_the_user_file_and_raises_changed()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        using DoNotDisturbService service = new(harness.Settings);
        await service.InitializeAsync();
        int changes = 0;
        service.Changed += () => Interlocked.Increment(ref changes);

        await service.SetAsync(true);

        Assert.True(service.IsEnabled);
        Assert.True(Volatile.Read(ref changes) >= 1);
        string? user = await harness.ReadAsync("/home/alice/.config/hackeros/notifications.config");
        Assert.Contains("doNotDisturb=true", user, StringComparison.Ordinal);

        await service.SetAsync(false);
        Assert.False(service.IsEnabled);
    }

    [Fact]
    public async Task An_edit_with_nano_applies_at_once()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        using DoNotDisturbService service = new(harness.Settings);
        await service.InitializeAsync();
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () => changed.TrySetResult();

        await harness.WriteAsUserAsync("/home/alice/.config/hackeros/notifications.config", "doNotDisturb=true\n");

        await changed.Task.WaitAsync(Patience);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public async Task The_mode_follows_the_session_user()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.LoginAsync(harness.Alice);
        using DoNotDisturbService service = new(harness.Settings);
        await service.InitializeAsync();
        await service.SetAsync(true);

        await harness.Session.LogoutAsync();
        await harness.LoginAsync(harness.Bob);
        await service.RefreshAsync();

        Assert.False(service.IsEnabled);
    }

    [Theory]
    [InlineData("doNotDisturb=true\n", true)]
    [InlineData("doNotDisturb=false\n", false)]
    [InlineData("doNotDisturb=maybe\n", false)]
    [InlineData("", false)]
    public async Task An_invalid_value_is_off(string content, bool expected)
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/hackeros/notifications.config", content);
        using DoNotDisturbService service = new(harness.Settings);

        await service.InitializeAsync();

        Assert.Equal(expected, service.IsEnabled);
    }
}
