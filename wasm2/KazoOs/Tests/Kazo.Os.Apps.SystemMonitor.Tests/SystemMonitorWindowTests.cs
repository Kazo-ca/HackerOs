using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.SystemMonitor.Tests;

public sealed class SystemMonitorWindowTests
{
    [Fact]
    public void Manifest_HasWindowKind_AndProcessCapabilities()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new SystemMonitorApp());
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.True(manifest.SingleInstancePerUser);
        Assert.Contains(AppCapabilities.ProcessList, manifest.Capabilities);
        Assert.Contains(AppCapabilities.ProcessManage, manifest.Capabilities);
        Assert.Contains(AppCapabilities.ServicesManage, manifest.Capabilities);
        // Every reading comes from /proc (ADR 0057, section 8).
        Assert.Contains(AppCapabilities.FileSystemSystemRead, manifest.Capabilities);
    }
}
