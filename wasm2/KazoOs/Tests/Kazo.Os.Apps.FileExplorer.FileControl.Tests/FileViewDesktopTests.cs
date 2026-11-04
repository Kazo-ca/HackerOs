namespace Kazo.Os.Apps.FileExplorer.FileControl.Tests;

/// <summary>The app drags the desktop accepts from the Start menu (ADR 0160 §6).</summary>
public sealed class FileViewDesktopTests
{
    [Fact]
    public void An_app_drag_names_one_app_or_several()
    {
        Assert.Equal(["org.kazoos.calculator"], FileView.ReadAppIds("""{"appId":"org.kazoos.calculator"}"""));
        Assert.Equal(["org.a", "org.b"], FileView.ReadAppIds("""{"appIds":["org.a","org.b"]}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[\"org.a\"]")]
    [InlineData("{\"appId\":42}")]
    public void Anything_else_names_no_app(string? json)
    {
        Assert.Empty(FileView.ReadAppIds(json));
    }
}
