using Kazo.Os.AppSdk.Terminal;
using Xunit;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>The terminal's interpretation of control sequences written by commands (ADR 0051, section 4).</summary>
public sealed class TerminalControlSequenceDecoderTests
{
    [Fact]
    public void Clear_sequence_becomes_one_erase_and_prints_nothing()
    {
        TerminalControlSequenceDecoder decoder = new();

        IReadOnlyList<TerminalOutputAction> actions = decoder.Decode("before\x1b[H\x1b[2J\x1b[3Jafter");

        Assert.Equal(
            [
                new TerminalOutputAction(TerminalOutputActionKind.Text, "before"),
                new TerminalOutputAction(TerminalOutputActionKind.EraseScreen, string.Empty),
                new TerminalOutputAction(TerminalOutputActionKind.Text, "after")
            ],
            actions);
    }

    [Fact]
    public void A_sequence_split_between_writes_is_completed_by_the_next_one()
    {
        TerminalControlSequenceDecoder decoder = new();

        IReadOnlyList<TerminalOutputAction> first = decoder.Decode("x\x1b[");
        IReadOnlyList<TerminalOutputAction> second = decoder.Decode("2Jy");

        Assert.Equal([new TerminalOutputAction(TerminalOutputActionKind.Text, "x")], first);
        Assert.Equal(TerminalOutputActionKind.EraseScreen, second[0].Kind);
        Assert.Equal("y", second[1].Text);
    }

    [Fact]
    public void Other_sequences_are_consumed_without_showing_garbage()
    {
        TerminalControlSequenceDecoder decoder = new();

        IReadOnlyList<TerminalOutputAction> actions = decoder.Decode("\x1b[1;31mred\x1b[0m \x1b[J\u001bcplain");

        Assert.Equal([new TerminalOutputAction(TerminalOutputActionKind.Text, "red plain")], actions);
    }
}
