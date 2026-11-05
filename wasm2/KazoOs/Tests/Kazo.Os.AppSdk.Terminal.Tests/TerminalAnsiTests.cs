using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Terminal;
using Xunit;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>The bytes the panel writes to its terminal: prompt, ONLCR, alternate-screen frames (ADR 0061).</summary>
public sealed class TerminalAnsiTests
{
    [Fact]
    public void Prompt_is_coloured_like_bash_and_measured_without_its_sequences()
    {
        (string text, int width) = TerminalAnsi.FormatPrompt("user@kazoos:~/src$");

        Assert.Equal("\x1b[1;32muser@kazoos\x1b[0m:\x1b[1;34m~/src\x1b[0m$ ", text);
        Assert.Equal("user@kazoos:~/src$ ".Length, width);
    }

    [Fact]
    public void An_unusual_prompt_is_printed_as_is()
    {
        Assert.Equal(("> ", 2), TerminalAnsi.FormatPrompt(">"));
    }

    [Theory]
    [InlineData("\x1b[1;32muser\x1b[0m:~$ ls\r\n", "user:~$ ls\n")]
    [InlineData("\x1b]0;title\x07plain\ttext", "plain\ttext")]
    [InlineData("\x1b[H\x1b[2J\x1b[3Jdone\x1b[?25h", "done")]
    [InlineData("bell\x07 and back\x08space", "bell and backspace")]
    public void Control_sequences_are_stripped_for_hosts(string text, string expected)
    {
        Assert.Equal(expected, TerminalAnsi.StripControlSequences(text));
    }

    [Fact]
    public void Newlines_become_carriage_return_line_feed_once()
    {
        Assert.Equal("a\r\nb\r\nc", TerminalAnsi.TranslateOutput("a\nb\r\nc"));
    }

    [Theory]
    [InlineData("text\r\n", false, true)]
    [InlineData("text", true, false)]
    [InlineData("done\r\n\x1b[0m", false, true)]
    [InlineData("\x1b[32m", true, true)]
    [InlineData("\x1b[32m", false, false)]
    [InlineData("\x1b[H\x1b[2J\x1b[3J", false, true)]
    public void Line_start_tracking_ignores_trailing_colours(string text, bool previous, bool expected)
    {
        Assert.Equal(expected, TerminalAnsi.EndsAtLineStart(text, previous));
    }

    [Fact]
    public void A_frame_is_drawn_in_place_cut_to_the_width_and_with_its_cursor()
    {
        VirtualScreen screen = new(8, 4);
        screen.Write("old\r\nrows\r\nhere\r\n!");
        TerminalScreenFrame frame = new(["  GNU nano", "hello"], new TerminalCursor(2, 1));

        screen.Write(TerminalAnsi.RenderFrame(frame, 8, 4));

        Assert.Equal("  GNU na", screen.Line(0));
        Assert.Equal("hello", screen.Line(1));
        Assert.Equal(string.Empty, screen.Line(2));
        Assert.Equal(string.Empty, screen.Line(3));
        Assert.Equal((1, 2), (screen.Row, screen.Column));
    }
}
