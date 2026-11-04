using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.IconViewer.Tests;

public sealed class IconViewerWindowTests
{
    [Fact]
    public void Manifest_HasWindowKind_AndNoCapabilities()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new IconViewerApp());
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.Equal("org.hackeros.icon-viewer", manifest.Id);
        Assert.Empty(manifest.Capabilities);
    }

    [Fact]
    public void Manifest_PassesValidation()
    {
        ManifestValidationResult result = AppManifestValidator.Validate(AppDefinitionMapper.ToManifest(new IconViewerApp()));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }
}
