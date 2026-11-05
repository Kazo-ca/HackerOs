using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Samples.WindowApp.Tests;

public sealed class SampleWindowAppTests
{
    [Fact]
    public void Manifest_HasWindowKindAndSingleInstance()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new WindowSampleApp());
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.True(manifest.SingleInstancePerUser);
        Assert.Contains(AppCapabilities.DialogFileOpen, manifest.Capabilities);
        Assert.Contains(AppCapabilities.DialogFileSave, manifest.Capabilities);
    }
}
