using Kazo.Os.Platform.Core.Shell;

namespace Kazo.Os.Platform.Core.Tests.Shell;

/// <summary>Grammar of the shell (ADR 0064, a POSIX sh subset; quoting rules of ADR 0014).</summary>
public sealed class ShellParserTests
{
    [Fact]
    public void Quotes_and_escapes_keep_spaces_inside_one_word()
    {
        ShellSimpleCommand command = Single("echo \"hello world\" 'foo bar' baz\\ qux \"\"");

        Assert.Equal(["echo", "hello world", "foo bar", "baz qux", ""], Texts(command));
    }

    [Fact]
    public void Double_quote_escapes_follow_adr_0014()
    {
        ShellSimpleCommand command = Single("echo \"a\\\"b\\\\c\\$d\\te\\q\"");

        Assert.Equal("a\"b\\c$d\te\\q", Texts(command)[1]);
    }

    [Fact]
    public void Blank_input_and_comments_are_empty_and_not_an_error()
    {
        Assert.True(ShellParser.Parse("   ").IsEmpty);
        Assert.True(ShellParser.Parse("# just a comment\n\n   # another").IsEmpty);
    }

    [Fact]
    public void Operators_need_no_spaces()
    {
        ShellCommandList list = Parse("ls|wc&&echo a>f>>g");

        ShellAndOrList andOr = list.Items.Single().AndOr;
        Assert.Equal(2, andOr.Pipelines.Count);
        Assert.Equal(2, andOr.Pipelines[0].Commands.Count);
        ShellSimpleCommand echo = Assert.IsType<ShellSimpleCommand>(andOr.Pipelines[1].Commands[0]);
        Assert.Equal(["echo", "a"], Texts(echo));
        Assert.Equal([ShellRedirectionKind.Truncate, ShellRedirectionKind.Append], echo.Redirections.Select(r => r.Kind));
        Assert.Equal(["f", "g"], echo.Redirections.Select(r => Literal(r.Target)));
    }

    [Fact]
    public void Lists_separate_with_semicolons_newlines_and_ampersands()
    {
        ShellCommandList list = Parse("a; b\nc & d || e");

        Assert.Equal(4, list.Items.Count);
        Assert.True(list.Items[2].Background);
        Assert.False(list.Items[3].Background);
        Assert.Equal([ShellAndOrOperator.Or], list.Items[3].AndOr.Operators);
        Assert.Equal("c", "a; b\nc & d || e".Substring(list.Items[2].Offset, list.Items[2].Length));
    }

    [Fact]
    public void A_command_may_be_only_a_redirection_or_only_assignments()
    {
        ShellSimpleCommand redirect = Single("> out.txt");
        ShellSimpleCommand assign = Single("A=1 B=\"x y\"");
        ShellSimpleCommand prefixed = Single("LANG=C sort f");

        Assert.Empty(redirect.Words);
        Assert.Single(redirect.Redirections);
        Assert.Equal(["A", "B"], assign.Assignments.Select(a => a.Name));
        Assert.Empty(assign.Words);
        Assert.Equal("LANG", prefixed.Assignments.Single().Name);
        Assert.Equal(["sort", "f"], Texts(prefixed));
        Assert.Equal(["echo", "A=1"], Texts(Single("echo A=1")));
        Assert.Equal(["A=1"], Texts(Single("'A=1'")));
    }

    [Fact]
    public void Descriptor_redirections_are_parsed()
    {
        ShellSimpleCommand command = Single("cmd 2>err.txt 2>&1 >&2 < in.txt &> all.txt");

        Assert.Equal(
            [
                (ShellRedirectionKind.Truncate, 2), (ShellRedirectionKind.DuplicateOutput, 2), (ShellRedirectionKind.DuplicateOutput, 1),
                (ShellRedirectionKind.Input, 0), (ShellRedirectionKind.Truncate, 1), (ShellRedirectionKind.DuplicateOutput, 2)
            ],
            command.Redirections.Select(r => (r.Kind, r.EffectiveDescriptor)));
        Assert.Equal(["cmd"], Texts(command));
        Assert.Equal(["echo", "a2"], Texts(Single("echo a2>f")));
    }

    [Fact]
    public void Here_documents_read_their_body_from_the_following_lines()
    {
        ShellCommandList list = Parse("cat <<EOF\nhello $USER\nEOF\ncat <<'RAW' | wc\n$HOME\n\tRAW\nRAW\necho done");

        ShellSimpleCommand first = Assert.IsType<ShellSimpleCommand>(list.Items[0].AndOr.Pipelines[0].Commands[0]);
        ShellRedirection here = first.Redirections.Single();
        Assert.Equal(ShellRedirectionKind.HereDocument, here.Kind);
        Assert.Contains(here.Target.Parts, part => part is ShellVariablePart { Name: "USER" });
        ShellSimpleCommand second = Assert.IsType<ShellSimpleCommand>(list.Items[1].AndOr.Pipelines[0].Commands[0]);
        Assert.Equal("$HOME\n\tRAW\n", Literal(second.Redirections.Single().Target));
        Assert.Equal(3, list.Items.Count);
        Assert.True(ShellParser.Parse("cat <<EOF").IsIncomplete);
    }

    [Fact]
    public void Compound_commands_build_their_trees()
    {
        ShellCommandList list = Parse(
            """
            if test -f a; then echo yes; elif false; then :; else echo no; fi
            for f in a b "c d"; do echo $f; done
            while false; do break; done
            until true; do continue; done
            case $x in a|b) echo ab ;; *) echo other ;; esac
            { echo grouped; } > out.txt
            (cd /tmp; ls)
            greet() { echo "hi $1"; }
            function bye { echo bye; }
            ! false
            """);

        ShellCommand[] commands = [.. list.Items.Select(item => item.AndOr.Pipelines[0].Commands[0])];
        ShellIfCommand @if = Assert.IsType<ShellIfCommand>(commands[0]);
        Assert.Equal(2, @if.Branches.Count);
        Assert.NotNull(@if.Else);
        ShellForCommand @for = Assert.IsType<ShellForCommand>(commands[1]);
        Assert.Equal("f", @for.Variable);
        Assert.Equal(3, @for.Items!.Count);
        Assert.False(Assert.IsType<ShellLoopCommand>(commands[2]).Until);
        Assert.True(Assert.IsType<ShellLoopCommand>(commands[3]).Until);
        ShellCaseCommand @case = Assert.IsType<ShellCaseCommand>(commands[4]);
        Assert.Equal(2, @case.Items.Count);
        Assert.Equal(2, @case.Items[0].Patterns.Count);
        ShellGroupCommand group = Assert.IsType<ShellGroupCommand>(commands[5]);
        Assert.False(group.Subshell);
        Assert.Single(group.Redirections);
        Assert.True(Assert.IsType<ShellGroupCommand>(commands[6]).Subshell);
        Assert.Equal("greet", Assert.IsType<ShellFunctionDefinition>(commands[7]).Name);
        Assert.Equal("bye", Assert.IsType<ShellFunctionDefinition>(commands[8]).Name);
        Assert.True(list.Items[9].AndOr.Pipelines[0].Negated);
    }

    [Fact]
    public void Reserved_words_are_plain_arguments_after_the_command_name()
    {
        Assert.Equal(["echo", "if", "then", "done", "}"], Texts(Single("echo if then done }")));
    }

    [Theory]
    [InlineData("| wc")]
    [InlineData("&& ls")]
    [InlineData("ls | | wc")]
    [InlineData("ls && | wc")]
    [InlineData("ls ;; wc")]
    [InlineData("fi")]
    [InlineData("a )")]
    [InlineData("if true; fi")]
    public void Misplaced_operators_and_words_are_syntax_errors(string line)
    {
        Assert.Equal(ShellSyntaxErrorCodes.UnexpectedToken, ShellParser.Parse(line).Error?.Code);
    }

    [Theory]
    [InlineData("ls |")]
    [InlineData("ls &&")]
    [InlineData("if true; then echo")]
    [InlineData("for x in a b; do")]
    [InlineData("echo $(ls")]
    [InlineData("echo \"abc")]
    public void Unfinished_input_is_reported_as_incomplete(string line)
    {
        ShellParseResult result = ShellParser.Parse(line);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsIncomplete, result.Error?.Code);
    }

    [Theory]
    [InlineData("echo >")]
    [InlineData("echo > | wc")]
    [InlineData("echo >> && ls")]
    public void A_redirection_needs_a_target(string line)
    {
        Assert.Equal(ShellSyntaxErrorCodes.MissingRedirectTarget, ShellParser.Parse(line).Error?.Code);
    }

    [Theory]
    [InlineData("a <> f", "<>")]
    [InlineData("a <<< x", "<<<")]
    public void Unsupported_operators_are_refused_with_a_clear_error(string line, string token)
    {
        ShellParseResult result = ShellParser.Parse(line);

        Assert.Equal(ShellSyntaxErrorCodes.Unsupported, result.Error?.Code);
        Assert.Contains($"`{token}'", result.Error!.Message);
    }

    [Fact]
    public void Operators_are_literal_when_quoted_or_escaped()
    {
        ShellSimpleCommand command = Single("echo ';' \"a || b\" \\< '$(x)' \"#\" a#b");

        Assert.Equal(["echo", ";", "a || b", "<", "$(x)", "#", "a#b"], Texts(command));
    }

    [Theory]
    [InlineData("echo 'abc", ShellSyntaxErrorCodes.UnterminatedQuote)]
    [InlineData("echo abc\\", ShellSyntaxErrorCodes.TrailingEscape)]
    [InlineData("echo ${HOME", ShellSyntaxErrorCodes.BadSubstitution)]
    [InlineData("echo ${1x}", ShellSyntaxErrorCodes.BadSubstitution)]
    [InlineData("echo `ls", ShellSyntaxErrorCodes.UnterminatedSubstitution)]
    public void Lexical_errors_are_structured(string line, string code)
    {
        ShellParseResult result = ShellParser.Parse(line);

        Assert.Equal(code, result.Error?.Code);
        Assert.True(result.Error!.Offset >= 5);
    }

    [Fact]
    public void Parameters_substitutions_and_tilde_are_recorded_not_substituted()
    {
        ShellSimpleCommand command = Single(
            "echo $HOME ${USER}x \"$?\" '$HOME' ~ ~/docs ~bob a~ \"~\" $ $5 \"$@\" ${#PATH} ${X:-def} $(ls -l) `pwd` $((1 + 2))");

        IReadOnlyList<ShellWord> words = command.Words;
        Assert.Equal(new ShellVariablePart("HOME"), words[1].Parts.Single());
        Assert.Equal([new ShellVariablePart("USER"), new ShellLiteralPart("x", false)], words[2].Parts);
        Assert.Equal(new ShellExitStatusPart(Quoted: true), words[3].Parts.Single());
        Assert.Equal(new ShellLiteralPart("$HOME", true), words[4].Parts.Single());
        Assert.IsType<ShellTildePart>(words[5].Parts.Single());
        Assert.Equal([new ShellTildePart(), new ShellLiteralPart("/docs", false)], words[6].Parts);
        Assert.Equal("~bob", Literal(words[7]));
        Assert.Equal("a~", Literal(words[8]));
        Assert.Equal(new ShellLiteralPart("~", true), words[9].Parts.Single());
        Assert.Equal("$", Literal(words[10]));
        Assert.Equal(new ShellSpecialParameterPart("5"), words[11].Parts.Single());
        Assert.Equal(new ShellSpecialParameterPart("@", Quoted: true), words[12].Parts.Single());
        Assert.Equal("length", Assert.IsType<ShellParameterExpansionPart>(words[13].Parts.Single()).Operator);
        ShellParameterExpansionPart fallback = Assert.IsType<ShellParameterExpansionPart>(words[14].Parts.Single());
        Assert.Equal((":-", "def"), (fallback.Operator, Literal(fallback.Argument!)));
        Assert.NotNull(Assert.IsType<ShellCommandSubstitutionPart>(words[15].Parts.Single()).Body);
        Assert.NotNull(Assert.IsType<ShellCommandSubstitutionPart>(words[16].Parts.Single()).Body);
        Assert.IsType<ShellArithmeticPart>(words[17].Parts.Single());
    }

    private static ShellCommandList Parse(string line)
    {
        ShellParseResult result = ShellParser.Parse(line);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Commands!;
    }

    private static ShellSimpleCommand Single(string line) =>
        Assert.IsType<ShellSimpleCommand>(Parse(line).Items.Single().AndOr.Pipelines.Single().Commands.Single());

    private static List<string> Texts(ShellSimpleCommand command) => [.. command.Words.Select(Literal)];

    private static string Literal(ShellWord word) =>
        string.Concat(word.Parts.Select(part => Assert.IsType<ShellLiteralPart>(part).Text));
}
