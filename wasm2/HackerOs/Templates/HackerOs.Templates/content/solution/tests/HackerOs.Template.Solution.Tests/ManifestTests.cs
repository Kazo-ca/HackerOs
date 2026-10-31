using HackerOs.App.Abstractions;

namespace HackerOs.Template.Solution.Tests;

/// <summary>The app's manifest class, the source of the generated manifest.json.</summary>
public sealed class ManifestTests
{
    private readonly TemplateSolutionApp _app = new();

    [Fact]
    public void The_app_is_a_window_app_with_its_id()
    {
        Assert.Equal("com.example.template-app", _app.Id);
        Assert.Equal(AppKind.Window, _app.Kind);
        Assert.Equal(typeof(MainWindow), _app.EntryPoint);
    }

    [Fact]
    public void The_app_only_asks_for_the_capabilities_it_needs()
    {
        Assert.Equal(new[] { AppCapabilities.FileSystemUserHomeRead }, _app.Capabilities);
    }
}
