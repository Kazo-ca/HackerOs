using HackerOs.AppSdk.Globalization;
using HackerOs.Platform.Core.Globalization;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Tests.Settings;

namespace HackerOs.Platform.Core.Tests.Globalization;

/// <summary>ADR 0193: datetime.config and the session's time zone the clock and the apps follow.</summary>
public sealed class SessionTimeZoneServiceTests
{
    private static readonly TimeZoneInfo Device = TimeZoneInfo.CreateCustomTimeZone("Test/Device", TimeSpan.FromHours(3), "Test device", "Test device");

    [Fact]
    public void Datetime_config_is_an_os_document_apps_may_open()
    {
        Assert.True(OsSettingsDocumentRegistry.TryGet("datetime", out OsSettingsDocument document));
        Assert.Equal("/etc/hackeros/datetime.config", document.SystemPath.Value);
    }

    [Fact]
    public async Task A_fresh_machine_ships_an_etc_file_and_the_zone_is_the_devices()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();

        string? system = await harness.ReadAsync("/etc/hackeros/datetime.config");
        Assert.NotNull(system);
        Assert.Contains("timeZone", system, StringComparison.Ordinal);

        using SessionTimeZoneService service = new(harness.Settings, Device);
        await service.InitializeAsync();

        Assert.Equal(HackTimeZone.Automatic, service.Setting);
        Assert.Same(Device, service.TimeZone);
    }

    [Fact]
    public async Task The_user_file_overrides_the_machine_and_a_reload_notifies()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/hackeros/datetime.config", "timeZone=Europe/Paris\n");
        await harness.LoginAsync(harness.Alice);
        using SessionTimeZoneService service = new(harness.Settings, Device);
        await service.InitializeAsync();
        Assert.Equal("Europe/Paris", HackTimeZone.GetId(service.TimeZone));

        int notifications = 0;
        service.Changed += () => notifications++;
        await harness.WriteAsRootAsync("/home/alice/.config/hackeros/datetime.config", "timeZone=America/Toronto\n");
        await service.RefreshAsync();

        Assert.Equal("America/Toronto", service.Setting);
        Assert.Equal("America/Toronto", HackTimeZone.GetId(service.TimeZone));
        Assert.True(notifications >= 1);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("Not/AZone")]
    [InlineData("")]
    public void Auto_and_unknown_names_mean_the_devices_zone(string setting)
    {
        using SessionTimeZoneService service = new(new SettingsFilesHarness().Settings, Device);
        service.Apply("UTC");

        service.Apply(setting);

        Assert.Same(Device, service.TimeZone);
    }

    [Fact]
    public void A_new_zone_notifies_once_and_the_same_zone_is_silent()
    {
        using SessionTimeZoneService service = new(new SettingsFilesHarness().Settings, Device);
        int notifications = 0;
        service.Changed += () => notifications++;

        service.Apply("Asia/Tokyo");
        service.Apply("Asia/Tokyo");

        Assert.Equal(1, notifications);
        DateTimeOffset utc = new(2026, 10, 29, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 23, 0, 0, TimeSpan.FromHours(9)), service.ToLocal(utc));
    }
}
