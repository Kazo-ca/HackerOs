using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

public sealed class SettingsScopeContractsTests
{
    [Fact]
    public void AppUser_key_projects_to_home_config_apps_path()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppUser("org.hackeros.terminal", "alice");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/home/alice/.config/apps/org.hackeros.terminal/settings.config", path.Value);
    }

    [Fact]
    public void AppDevice_key_projects_to_device_partitioned_path()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppDevice("org.hackeros.terminal", "device-1");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/var/lib/hackeros/devices/device-1/apps/org.hackeros.terminal/settings.config", path.Value);
    }

    [Fact]
    public void AppUserDevice_key_projects_to_user_partition_inside_device()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppUserDevice(
            "org.hackeros.terminal", "alice", "device-1", "window-geometry");

        Assert.Equal(SettingsScope.AppUserDevice, key.Scope);
        Assert.Equal("alice", key.UserId);
        Assert.Equal("device-1", key.InstallationId);
        Assert.Equal(
            "/var/lib/hackeros/devices/device-1/users/alice/apps/org.hackeros.terminal/window-geometry.config",
            SettingsDocumentPathFactory.GetProjectionPath(key).Value);
    }

    [Fact]
    public void AppRoamingUser_key_projects_under_roaming_subpath()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppRoamingUser("org.hackeros.terminal", "alice");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/home/alice/.config/apps/org.hackeros.terminal/roaming/settings.config", path.Value);
    }

    [Fact]
    public void OsAdmin_key_projects_under_protected_etc_hackeros()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForOsAdmin("policy");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/etc/hackeros/policy.config", path.Value);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    public void Identifiers_reject_traversal_separators_and_dot_segments(string invalidId)
    {
        Assert.Throws<ArgumentException>(() => SettingsDocumentKey.ForAppUser("org.hackeros.terminal", invalidId));
        Assert.Throws<ArgumentException>(() => SettingsDocumentKey.ForOsAdmin(invalidId));
    }

    [Fact]
    public void AppId_cannot_be_empty()
    {
        Assert.Throws<ArgumentException>(() => SettingsDocumentKey.ForAppUser(" ", "alice"));
    }
}
