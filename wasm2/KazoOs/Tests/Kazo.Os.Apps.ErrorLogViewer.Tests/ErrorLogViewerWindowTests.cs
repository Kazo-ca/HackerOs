using Kazo.Os.App.Abstractions;
using Xunit;

namespace Kazo.Os.Apps.ErrorLogViewer.Tests;

public sealed class ErrorLogViewerWindowTests
{
    [Fact]
    public void Manifest_HasWindowKind_AndReadsLogsThroughFileRights()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new ErrorLogViewerApp());
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.True(manifest.SingleInstancePerUser);
        Assert.Contains(AppCapabilities.FileSystemSystemRead, manifest.Capabilities);
        Assert.Contains(AppCapabilities.FileSystemSystemWrite, manifest.Capabilities);
        Assert.DoesNotContain("diagnostics.read", manifest.Capabilities);
    }

    [Fact]
    public void Generated_manifest_json_matches_the_definition()
    {
        AppManifest fromJson = AppManifestJsonSerializer.DeserializeStrict(File.ReadAllText(FindManifestPath()));
        AppManifest fromDefinition = AppDefinitionMapper.ToManifest(new ErrorLogViewerApp());
        Assert.Equal(fromDefinition.Capabilities, fromJson.Capabilities);
        Assert.Equal(fromDefinition.Id, fromJson.Id);
    }

    private static string FindManifestPath()
    {
        for (DirectoryInfo? directory = new(System.AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "Apps", "System", "Kazo.Os.Apps.ErrorLogViewer", "manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("manifest.json of Error Log Viewer not found above the test output.");
    }
}
