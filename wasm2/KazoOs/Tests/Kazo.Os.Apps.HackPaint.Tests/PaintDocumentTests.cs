using Xunit;

namespace Kazo.Os.Apps.HackPaint.Tests;

/// <summary>Unsaved-changes tracking and the title shown in the window.</summary>
public sealed class PaintDocumentTests
{
    [Fact]
    public void A_new_document_is_clean_until_drawn_on_and_clean_again_once_saved()
    {
        PaintCanvasState state = new();
        Assert.False(state.IsDirty);

        state.DrawLine(1, 1, 20, 20);
        Assert.True(state.IsDirty);

        state.MarkSaved();
        Assert.False(state.IsDirty);

        state.DrawLine(5, 5, 6, 6);
        state.Undo();
        Assert.False(state.IsDirty);
    }

    [Fact]
    public void Opening_an_image_starts_clean()
    {
        PaintCanvasState state = new();
        state.DrawLine(1, 1, 20, 20);

        state.ReplaceDocument(2, 2, new byte[2 * 2 * 4]);

        Assert.False(state.IsDirty);
        Assert.Equal(2, state.Width);
    }

    [Theory]
    [InlineData(null, false, "Untitled — Hack Paint")]
    [InlineData("/home/alice/Pictures/cat.png", true, "*cat.png — Hack Paint")]
    public void Title_shows_the_file_name_and_unsaved_marker(string? path, bool dirty, string expected) =>
        Assert.Equal(expected, PaintTitle.Format(path, dirty));
}
