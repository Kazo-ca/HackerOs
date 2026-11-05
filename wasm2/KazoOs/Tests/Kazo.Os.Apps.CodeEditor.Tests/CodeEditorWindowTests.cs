using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.CodeEditor.Tests;

public sealed class CodeEditorWindowTests
{
    [Fact]
    public void Manifest_HasWindowKind_AndRequiredCapabilities()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new CodeEditorApp());
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.False(manifest.SingleInstancePerUser);
        Assert.Contains(AppCapabilities.FileSystemUserHomeRead, manifest.Capabilities);
        Assert.Contains(AppCapabilities.FileSystemUserHomeWrite, manifest.Capabilities);
        Assert.Contains(AppCapabilities.DialogFileOpen, manifest.Capabilities);
        Assert.Contains(AppCapabilities.DialogFileSave, manifest.Capabilities);
        Assert.Contains(AppCapabilities.ClipboardRead, manifest.Capabilities);
        Assert.Contains(AppCapabilities.ClipboardWrite, manifest.Capabilities);
    }
}
