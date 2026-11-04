using Xunit;
using Kazo.Os.AppSdk.Terminal;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>The standard input a terminal gives its commands (ADR 0064): a tty in canonical mode.</summary>
public sealed class TerminalInputReaderTests
{
    [Fact]
    public async Task A_read_waits_for_a_finished_line()
    {
        TerminalInputReader input = new();

        ValueTask<string?> pending = input.ReadLineAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);
        input.Push("hello\n");

        Assert.Equal("hello", await pending);
    }

    [Fact]
    public async Task Ctrl_d_on_an_empty_line_is_one_end_of_input()
    {
        TerminalInputReader input = new();
        input.Push("a\n");
        input.PushEndOfInput();

        Assert.Equal("a\n", await input.ReadToEndAsync());
        // The end was consumed: a later read waits again, as on a Linux tty.
        ValueTask<string?> next = input.ReadLineAsync(CancellationToken.None);
        Assert.False(next.IsCompleted);
        input.PushEndOfInput();
        Assert.Null(await next);
    }

    [Fact]
    public async Task Closing_ends_every_read_and_hands_back_unread_input()
    {
        TerminalInputReader input = new();
        input.Push("ls -l\nsec");

        Assert.Equal("ls -l", await input.ReadLineAsync(CancellationToken.None));
        input.CloseInput();

        Assert.Equal("sec", input.TakeUnread());
        Assert.Null(await input.ReadLineAsync(CancellationToken.None));
    }
}

/// <summary>Multi-line commands folded into one history entry (ADR 0064).</summary>
public sealed class TerminalHistoryTests
{
    [Theory]
    [InlineData("ls", "ls")]
    [InlineData("for f in a b\ndo\necho $f\ndone", "for f in a b; do echo $f; done")]
    [InlineData("if true; then\necho yes\nfi", "if true; then echo yes; fi")]
    [InlineData("ls |\nwc -l", "ls | wc -l")]
    [InlineData("f() {\necho hi\n}", "f() { echo hi; }")]
    public void Continuation_lines_join_like_bash_cmdhist(string typed, string expected)
    {
        Assert.Equal(expected, TerminalHistory.ToSingleLine(typed));
    }
}
