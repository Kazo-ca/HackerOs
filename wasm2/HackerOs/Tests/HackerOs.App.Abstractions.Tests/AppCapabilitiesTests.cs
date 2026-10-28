namespace HackerOs.App.Abstractions.Tests;

public sealed class AppCapabilitiesTests
{
    [Theory]
    [InlineData(AppCapabilities.ProcessList)]
    [InlineData(AppCapabilities.ProcessManage)]
    [InlineData(AppCapabilities.NotificationsPost)]
    [InlineData(AppCapabilities.WindowsManage)]
    [InlineData(AppCapabilities.ClipboardRead)]
    [InlineData(AppCapabilities.ClipboardWrite)]
    [InlineData(AppCapabilities.ServicesManage)]
    public void Process_notification_window_clipboard_and_service_capabilities_are_known(string capability)
    {
        Assert.True(AppCapabilities.IsKnown(capability));
        Assert.Contains(capability, AppCapabilities.All);
    }

    [Theory]
    [InlineData(AppCapabilities.DialogFileOpen)]
    [InlineData(AppCapabilities.DialogFileSave)]
    [InlineData(AppCapabilities.DialogFolderSelect)]
    public void Dialog_capabilities_require_window_kind(string capability)
    {
        Assert.True(AppCapabilities.RequiresWindowKind(capability));
    }

    [Theory]
    [InlineData(AppCapabilities.FileSystemPrivateRead)]
    [InlineData(AppCapabilities.ProcessManage)]
    [InlineData(AppCapabilities.NotificationsPost)]
    public void Non_dialog_capabilities_do_not_require_window_kind(string capability)
    {
        Assert.False(AppCapabilities.RequiresWindowKind(capability));
    }

    /// <summary>The exact classification table of ADR 0049 section 1.</summary>
    public static TheoryData<string, CapabilityRisk> Classification => new()
    {
        { AppCapabilities.FileSystemPrivateRead, CapabilityRisk.Normal },
        { AppCapabilities.FileSystemPrivateWrite, CapabilityRisk.Normal },
        { AppCapabilities.FileSystemUserSelectedRead, CapabilityRisk.Normal },
        { AppCapabilities.FileSystemUserSelectedWrite, CapabilityRisk.Normal },
        { AppCapabilities.FileSystemUserHomeRead, CapabilityRisk.Normal },
        { AppCapabilities.FileSystemUserHomeWrite, CapabilityRisk.Normal },
        { AppCapabilities.FileSystemSystemRead, CapabilityRisk.Dangerous },
        { AppCapabilities.FileSystemSystemWrite, CapabilityRisk.Dangerous },
        { AppCapabilities.AppsLaunch, CapabilityRisk.Normal },
        { AppCapabilities.AppsManage, CapabilityRisk.Dangerous },
        { AppCapabilities.DialogFileOpen, CapabilityRisk.Normal },
        { AppCapabilities.DialogFileSave, CapabilityRisk.Normal },
        { AppCapabilities.DialogFolderSelect, CapabilityRisk.Normal },
        { AppCapabilities.FileAssociationsRead, CapabilityRisk.Normal },
        { AppCapabilities.FileAssociationsWrite, CapabilityRisk.Dangerous },
        { AppCapabilities.ProcessList, CapabilityRisk.Normal },
        { AppCapabilities.ProcessManage, CapabilityRisk.Dangerous },
        { AppCapabilities.ServicesManage, CapabilityRisk.Dangerous },
        { AppCapabilities.WindowsManage, CapabilityRisk.Dangerous },
        { AppCapabilities.NotificationsPost, CapabilityRisk.Normal },
        { AppCapabilities.ClipboardRead, CapabilityRisk.Normal },
        { AppCapabilities.ClipboardWrite, CapabilityRisk.Normal },
        { AppCapabilities.NetworkSimulatedRead, CapabilityRisk.Normal },
        { AppCapabilities.NetworkSimulatedWrite, CapabilityRisk.Normal },
        { AppCapabilities.NetworkRealAccess, CapabilityRisk.Dangerous },
        { AppCapabilities.NetworkListen, CapabilityRisk.Normal },
        { AppCapabilities.NetworkListenPrivileged, CapabilityRisk.Dangerous },
        { AppCapabilities.ShellControl, CapabilityRisk.Normal },
        { AppCapabilities.ShellContextMenu, CapabilityRisk.Normal },
        { AppCapabilities.ScreensManage, CapabilityRisk.Normal },
        { AppCapabilities.PermissionsManage, CapabilityRisk.Dangerous },
        { AppCapabilities.ServerConnectionManage, CapabilityRisk.Dangerous },
        { AppCapabilities.SystemPower, CapabilityRisk.Dangerous },
        { AppCapabilities.SystemStorageInspect, CapabilityRisk.Dangerous },
        { AppCapabilities.SystemBackup, CapabilityRisk.Dangerous },
        { AppCapabilities.ShellHost, CapabilityRisk.Dangerous },
        { AppCapabilities.KeyringReadAll, CapabilityRisk.Dangerous },
        { AppCapabilities.UsersManage, CapabilityRisk.Dangerous },
    };

    [Theory]
    [MemberData(nameof(Classification))]
    public void Every_capability_has_the_risk_decided_by_ADR_0049(string capability, CapabilityRisk risk)
    {
        Assert.Equal(risk, AppCapabilities.GetRisk(capability));
        Assert.Equal(risk == CapabilityRisk.Dangerous, AppCapabilities.IsDangerous(capability));
    }

    [Fact]
    public void The_catalog_is_exactly_the_classified_capabilities()
    {
        // A new capability must be added to the classification table above (ADR 0049 section 1).
        Assert.Equal(
            Classification.Select(row => (string)row[0]).Order(StringComparer.Ordinal),
            AppCapabilities.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_catalog_capability_is_described_for_the_approval_window()
    {
        Assert.All(AppCapabilities.All, capability =>
        {
            CapabilityDescription description = AppCapabilities.Describe(capability);
            Assert.Equal(capability, description.Capability);
            Assert.False(string.IsNullOrWhiteSpace(description.Title));
            Assert.Equal(AppCapabilities.GetRisk(capability), description.Risk);
            Assert.Equal(description.Risk == CapabilityRisk.Dangerous, !string.IsNullOrWhiteSpace(description.RiskDescription));
        });
    }

    [Fact]
    public void Dangerous_description_carries_the_risk_sentence_shown_to_the_administrator()
    {
        CapabilityDescription description = AppCapabilities.Describe(AppCapabilities.NetworkRealAccess);

        Assert.Equal("Joindre de vrais serveurs sur Internet par le serveur relais.", description.RiskDescription);
    }

    [Theory]
    [InlineData("settings.read")]
    [InlineData("settings.write")]
    [InlineData("settings.system.read")]
    [InlineData("settings.system.write")]
    [InlineData("diagnostics.read")]
    [InlineData("diagnostics.clear")]
    public void Settings_and_diagnostics_capabilities_left_the_catalog(string capability)
    {
        Assert.False(AppCapabilities.IsKnown(capability));
        Assert.False(AppCapabilities.IsDangerous(capability));
        Assert.Throws<ArgumentException>(() => AppCapabilities.GetRisk(capability));
        Assert.Throws<ArgumentException>(() => AppCapabilities.Describe(capability));
    }

    [Fact]
    public void Topic_permissions_are_outside_the_catalog_and_never_dangerous()
    {
        Assert.False(AppCapabilities.IsDangerous("topic-subscribe:app/org.example.scanner/events"));
    }
}
