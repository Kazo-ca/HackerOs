using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Policy;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;

namespace Kazo.Os.Platform.Core.Tests.Policy;

/// <summary>The administrator policy is the system-only file /etc/kazoos/policy.config (ADR 0044).</summary>
public sealed class PolicySettingsDocumentsTests
{
    private const string RuntimeInstall = "allowRuntimePackageInstall";

    [Fact]
    public async Task Ordinary_user_cannot_write_the_policy_even_with_the_system_write_capability()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        AuthenticatedPrincipal alice = await harness.LoginAsync(harness.Alice);
        IAppFileSystemGateway settingsApp = harness.CreateAppGateway(
            alice, "org.kazoos.settings", AppCapabilities.FileSystemSystemRead, AppCapabilities.FileSystemSystemWrite);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.Settings
            .SetAsync(PolicySettingsDocuments.Document, RuntimeInstall, "true", SettingLayer.System, settingsApp)
            .AsTask());

        LayeredConfigSnapshot policy = await harness.Settings.ReadAsync(PolicySettingsDocuments.Document);
        Assert.Equal("false", policy.Values[RuntimeInstall]);
    }

    [Fact]
    public void Never_evaluated_capabilityGrantsLocked_key_is_gone_from_the_schema()
    {
        // ADR 0049 section 3: dangerous capabilities are governed by security.config instead.
        Assert.DoesNotContain("capabilityGrantsLocked", PolicySettingsDocuments.Schema.Fields.Keys);
        Assert.Contains(RuntimeInstall, PolicySettingsDocuments.Schema.Fields.Keys);
    }

    [Fact]
    public async Task Policy_has_no_user_layer()
    {
        SettingsFilesHarness harness = new();
        await harness.LoginAsync(harness.Alice);
        await harness.WriteAsRootAsync("/home/alice/.config/kazoos/policy.config", "allowRuntimePackageInstall=true\n");

        LayeredConfigSnapshot policy = await harness.Settings.ReadAsync(PolicySettingsDocuments.Document);

        Assert.Equal("false", policy.Values[RuntimeInstall]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Settings
            .SetAsync(PolicySettingsDocuments.Document, RuntimeInstall, "true")
            .AsTask());
    }

    [Fact]
    public async Task Root_edit_of_etc_is_what_every_reader_sees()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync(
            PolicySettingsDocuments.Document.SystemPath.Value,
            "allowRuntimePackageInstall=true\n");

        LayeredConfigSnapshot policy = await harness.Settings.ReadAsync(PolicySettingsDocuments.Document);

        Assert.Equal("true", policy.Values[RuntimeInstall]);
        Assert.Equal(SettingLayer.System, policy.Sources[RuntimeInstall]);
        Assert.Empty(policy.Warnings);
    }

    [Fact]
    public async Task Invalid_value_in_the_file_falls_back_to_the_default_with_a_warning()
    {
        SettingsFilesHarness harness = new();
        await harness.WriteAsRootAsync(PolicySettingsDocuments.Document.SystemPath.Value, "allowRuntimePackageInstall=not-a-bool\n");

        LayeredConfigSnapshot policy = await harness.Settings.ReadAsync(PolicySettingsDocuments.Document);

        Assert.Equal("false", policy.Values[RuntimeInstall]);
        LayeredConfigWarning warning = Assert.Single(policy.Warnings);
        Assert.Equal("settings: /etc/kazoos/policy.config:1: invalid value for 'allowRuntimePackageInstall' (expected boolean), using default", warning.ToLogLine());
    }

    [Fact]
    public async Task Writing_an_invalid_value_is_rejected_before_touching_the_file()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        string? before = await harness.ReadAsync(PolicySettingsDocuments.Document.SystemPath.Value);

        await Assert.ThrowsAsync<ArgumentException>(() => harness.Settings
            .SetAsync(PolicySettingsDocuments.Document, RuntimeInstall, "maybe", SettingLayer.System, harness.CreateRootGateway())
            .AsTask());

        Assert.Equal(before, await harness.ReadAsync(PolicySettingsDocuments.Document.SystemPath.Value));
    }
}
