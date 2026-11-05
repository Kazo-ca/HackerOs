using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Terminal;
using Xunit;

namespace Kazo.Os.AppSdk.Terminal.Tests;

/// <summary>Decoding of the keyboard bytes the terminal emulator sends (ADR 0061).</summary>
public sealed class TerminalInputDecoderTests
{
    [Fact]
    public void Printable_text_becomes_one_character_key_each()
    {
        IReadOnlyList<TerminalKeyEvent> keys = TerminalInputDecoder.Decode("ls");

        Assert.Equal(
            [new TerminalKeyEvent(TerminalKey.Character, "l"), new TerminalKeyEvent(TerminalKey.Character, "s")],
            keys);
    }

    [Theory]
    [InlineData("\r", TerminalKey.Enter)]
    [InlineData("\x7f", TerminalKey.Backspace)]
    [InlineData("\b", TerminalKey.Backspace)]
    [InlineData("\x1b", TerminalKey.Escape)]
    [InlineData("\x1b[A", TerminalKey.ArrowUp)]
    [InlineData("\x1b[B", TerminalKey.ArrowDown)]
    [InlineData("\x1b[C", TerminalKey.ArrowRight)]
    [InlineData("\x1b[D", TerminalKey.ArrowLeft)]
    [InlineData("\x1bOA", TerminalKey.ArrowUp)]
    [InlineData("\x1b[H", TerminalKey.Home)]
    [InlineData("\x1b[F", TerminalKey.End)]
    [InlineData("\x1b[1~", TerminalKey.Home)]
    [InlineData("\x1b[4~", TerminalKey.End)]
    [InlineData("\x1b[3~", TerminalKey.Delete)]
    [InlineData("\x1b[5~", TerminalKey.PageUp)]
    [InlineData("\x1b[6~", TerminalKey.PageDown)]
    public void Special_keys_are_decoded(string data, TerminalKey expected)
    {
        TerminalKeyEvent key = Assert.Single(TerminalInputDecoder.Decode(data));

        Assert.Equal(expected, key.Key);
    }

    [Fact]
    public void Control_letters_carry_the_letter_and_the_control_flag()
    {
        TerminalKeyEvent key = Assert.Single(TerminalInputDecoder.Decode("\x03"));

        Assert.Equal(new TerminalKeyEvent(TerminalKey.Character, "c", Control: true), key);
    }

    [Fact]
    public void Tab_is_a_tab_character()
    {
        TerminalKeyEvent key = Assert.Single(TerminalInputDecoder.Decode("\t"));

        Assert.Equal(new TerminalKeyEvent(TerminalKey.Character, "\t"), key);
    }

    [Fact]
    public void Modifier_parameters_set_the_flags()
    {
        TerminalKeyEvent key = Assert.Single(TerminalInputDecoder.Decode("\x1b[1;5D"));

        Assert.Equal(new TerminalKeyEvent(TerminalKey.ArrowLeft, Control: true), key);
    }

    [Fact]
    public void Escape_prefix_is_alt()
    {
        TerminalKeyEvent key = Assert.Single(TerminalInputDecoder.Decode("\x1b" + "b"));

        Assert.Equal(new TerminalKeyEvent(TerminalKey.Character, "b", Alt: true), key);
    }

    [Fact]
    public void A_pasted_block_splits_into_keys_and_one_enter_per_line()
    {
        IReadOnlyList<TerminalKeyEvent> keys = TerminalInputDecoder.Decode("a\r\nb\n");

        Assert.Equal(
            [
                new TerminalKeyEvent(TerminalKey.Character, "a"),
                new TerminalKeyEvent(TerminalKey.Enter),
                new TerminalKeyEvent(TerminalKey.Character, "b"),
                new TerminalKeyEvent(TerminalKey.Enter)
            ],
            keys);
    }

    [Fact]
    public void Unknown_sequences_are_dropped_and_decoding_continues()
    {
        IReadOnlyList<TerminalKeyEvent> keys = TerminalInputDecoder.Decode("\x1b[200~x");

        Assert.Equal([new TerminalKeyEvent(TerminalKey.Character, "x")], keys);
    }

    [Fact]
    public void Surrogate_pairs_stay_one_character()
    {
        TerminalKeyEvent key = Assert.Single(TerminalInputDecoder.Decode("😀"));

        Assert.Equal("😀", key.Text);
    }
}
