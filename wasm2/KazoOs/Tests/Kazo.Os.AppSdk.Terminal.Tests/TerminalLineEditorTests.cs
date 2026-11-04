using Kazo.Os.AppSdk.Terminal;
using Xunit;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>Readline-like editing of the prompt line and its redraw on a character grid (ADR 0061).</summary>
public sealed class TerminalLineEditorTests
{
    private const string Prompt = "$ ";

    [Fact]
    public void Insertion_in_the_middle_redraws_the_line_and_keeps_the_cursor()
    {
        VirtualScreen screen = new(20, 5);
        TerminalLineEditor editor = new();
        editor.Insert("lsal");
        screen.Write(editor.Render(Prompt, 2, 20));
        editor.Move(-2);
        editor.Insert(" -");
        screen.Write(editor.Render(Prompt, 2, 20));

        Assert.Equal("$ ls -al", screen.Line(0));
        Assert.Equal((0, 6), (screen.Row, screen.Column));
        Assert.Equal("ls -al", editor.Text);
    }

    [Fact]
    public void A_line_wider_than_the_terminal_wraps_and_redraws_from_the_prompt()
    {
        VirtualScreen screen = new(10, 5);
        TerminalLineEditor editor = new();
        editor.Insert("abcdefghijklmn");
        screen.Write(editor.Render(Prompt, 2, 10));
        Assert.Equal("$ abcdefgh", screen.Line(0));
        Assert.Equal("ijklmn", screen.Line(1));
        Assert.Equal((1, 6), (screen.Row, screen.Column));

        editor.MoveToStart();
        screen.Write(editor.Render(Prompt, 2, 10));
        Assert.Equal((0, 2), (screen.Row, screen.Column));

        editor.Delete();
        screen.Write(editor.Render(Prompt, 2, 10));
        Assert.Equal("$ bcdefghi", screen.Line(0));
        Assert.Equal("jklmn", screen.Line(1));
        Assert.Equal((0, 2), (screen.Row, screen.Column));
    }

    [Fact]
    public void Shrinking_a_wrapped_line_erases_the_rows_it_left()
    {
        VirtualScreen screen = new(10, 5);
        TerminalLineEditor editor = new();
        editor.Insert("abcdefghijkl");
        screen.Write(editor.Render(Prompt, 2, 10));
        editor.KillToStart();
        screen.Write(editor.Render(Prompt, 2, 10));

        Assert.Equal("$", screen.Line(0));
        Assert.Equal(string.Empty, screen.Line(1));
        Assert.Equal((0, 2), (screen.Row, screen.Column));
    }

    [Fact]
    public void A_line_ending_exactly_at_the_right_edge_puts_the_cursor_on_the_next_row()
    {
        VirtualScreen screen = new(10, 5);
        TerminalLineEditor editor = new();
        editor.Insert("abcdefgh");
        screen.Write(editor.Render(Prompt, 2, 10));
        Assert.Equal((1, 0), (screen.Row, screen.Column));

        editor.Backspace();
        screen.Write(editor.Render(Prompt, 2, 10));
        Assert.Equal("$ abcdefg", screen.Line(0));
        Assert.Equal((0, 9), (screen.Row, screen.Column));
    }

    [Fact]
    public void Commit_moves_below_the_whole_line_and_starts_an_empty_one()
    {
        VirtualScreen screen = new(10, 5);
        TerminalLineEditor editor = new();
        editor.Insert("abcdefghijkl");
        editor.MoveToStart();
        screen.Write(editor.Render(Prompt, 2, 10));
        screen.Write(editor.Commit(2, 10));

        Assert.Equal((2, 0), (screen.Row, screen.Column));
        Assert.True(editor.IsEmpty);
    }

    [Fact]
    public void Kill_and_word_commands_edit_like_readline()
    {
        TerminalLineEditor editor = new();
        editor.Insert("git commit -m msg");

        Assert.True(editor.DeleteWordBefore());
        Assert.Equal("git commit -m ", editor.Text);

        editor.Move(-3);
        Assert.True(editor.KillToEnd());
        Assert.Equal("git commit ", editor.Text);

        Assert.True(editor.KillToStart());
        Assert.Equal(string.Empty, editor.Text);
        Assert.False(editor.Backspace());
    }

    [Fact]
    public void SetText_places_the_cursor_where_asked()
    {
        TerminalLineEditor editor = new();
        editor.SetText("cat file", 3);

        Assert.Equal(3, editor.Cursor);
        editor.SetText("ls");
        Assert.Equal(2, editor.Cursor);
    }
}
