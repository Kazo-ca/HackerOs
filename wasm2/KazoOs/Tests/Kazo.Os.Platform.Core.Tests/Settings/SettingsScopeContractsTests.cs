using Kazo.Os.App.Abstractions;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Settings;

public sealed class SettingsScopeContractsTests
{
    [Fact]
    public void AppUser_key_projects_to_home_config_apps_path()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppUser("org.kazoos.terminal", "alice");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/home/alice/.config/apps/org.kazoos.terminal/settings.config", path.Value);
    }

    [Fact]
    public void AppDevice_key_projects_to_device_partitioned_path()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppDevice("org.kazoos.terminal", "device-1");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/var/lib/kazoos/devices/device-1/apps/org.kazoos.terminal/settings.config", path.Value);
    }

    [Fact]
    public void AppUserDevice_key_projects_to_user_partition_inside_device()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppUserDevice(
            "org.kazoos.terminal", "alice", "device-1", "window-geometry");

        Assert.Equal(SettingsScope.AppUserDevice, key.Scope);
        Assert.Equal("alice", key.UserId);
        Assert.Equal("device-1", key.InstallationId);
        Assert.Equal(
            "/var/lib/kazoos/devices/device-1/users/alice/apps/org.kazoos.terminal/window-geometry.config",
            SettingsDocumentPathFactory.GetProjectionPath(key).Value);
    }

    [Fact]
    public void AppRoamingUser_key_projects_under_roaming_subpath()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForAppRoamingUser("org.kazoos.terminal", "alice");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/home/alice/.config/apps/org.kazoos.terminal/roaming/settings.config", path.Value);
    }

    [Fact]
    public void OsAdmin_key_projects_under_protected_etc_kazoos()
    {
        SettingsDocumentKey key = SettingsDocumentKey.ForOsAdmin("policy");

        VirtualPath path = SettingsDocumentPathFactory.GetProjectionPath(key);

        Assert.Equal("/etc/kazoos/policy.config", path.Value);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    public void Identifiers_reject_traversal_separators_and_dot_segments(string invalidId)
    {
        Assert.Throws<ArgumentException>(() => SettingsDocumentKey.ForAppUser("org.kazoos.terminal", invalidId));
        Assert.Throws<ArgumentException>(() => SettingsDocumentKey.ForOsAdmin(invalidId));
    }

    [Fact]
    public void AppId_cannot_be_empty()
    {
        Assert.Throws<ArgumentException>(() => SettingsDocumentKey.ForAppUser(" ", "alice"));
    }
}
