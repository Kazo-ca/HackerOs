namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// A parsed program (ADR 0064, superseding the grammar of ADR 0050 section 2): a command line or a
/// whole script, as a sequence of and-or lists separated by <c>;</c>, <c>&amp;</c> or newlines.
/// </summary>
/// <param name="Items">The and-or lists in source order.</param>
public sealed record ShellCommandList(IReadOnlyList<ShellListItem> Items);

/// <summary>One and-or list of a <see cref="ShellCommandList"/>, run in the foreground or, with a trailing <c>&amp;</c>, as a background job.</summary>
/// <param name="AndOr">The pipelines joined by <c>&amp;&amp;</c> and <c>||</c>.</param>
/// <param name="Background"><see langword="true"/> when the list ends with <c>&amp;</c>.</param>
/// <param name="Offset">Offset of the list's first character in the source, for <c>jobs</c>.</param>
/// <param name="Length">Number of source characters the list spans.</param>
public sealed record ShellListItem(ShellAndOrList AndOr, bool Background, int Offset, int Length);

/// <summary>How two pipelines of an and-or list are joined.</summary>
public enum ShellAndOrOperator
{
    /// <summary><c>&amp;&amp;</c>: the next pipeline runs only when the previous one exited with 0.</summary>
    And,

    /// <summary><c>||</c>: the next pipeline runs only when the previous one failed.</summary>
    Or
}

/// <summary>Pipelines joined by <c>&amp;&amp;</c> and <c>||</c>, evaluated left to right with equal precedence.</summary>
/// <param name="Pipelines">The pipelines in source order.</param>
/// <param name="Operators">The operator before each pipeline after the first (<c>Operators[i]</c> joins pipeline <c>i</c> and <c>i + 1</c>).</param>
public sealed record ShellAndOrList(IReadOnlyList<ShellPipeline> Pipelines, IReadOnlyList<ShellAndOrOperator> Operators);

/// <summary>One pipeline: commands joined by <c>|</c>, whose stages run at the same time.</summary>
/// <param name="Commands">Stages in source order; each stage's output feeds the next stage's input.</param>
/// <param name="Negated"><see langword="true"/> for <c>! pipeline</c>: the exit status is inverted.</param>
public sealed record ShellPipeline(IReadOnlyList<ShellCommand> Commands, bool Negated = false);

/// <summary>A command of a pipeline: a simple command, a compound command or a function definition.</summary>
public abstract record ShellCommand
{
    /// <summary>Gets the redirections applied to the whole command, in source order.</summary>
    public IReadOnlyList<ShellRedirection> Redirections { get; init; } = [];
}

/// <summary><c>NAME=value</c> before a command name, or alone.</summary>
/// <param name="Name">Variable name.</param>
/// <param name="Value">Unexpanded value (tilde, parameter, command and arithmetic expansion; never split nor globbed).</param>
public sealed record ShellAssignment(string Name, ShellWord Value);

/// <summary>
/// One simple command: its prefix assignments, its words (command name first) and its
/// redirections. A command made only of assignments sets shell variables; one made only of
/// redirections opens (and closes) its files, as in POSIX shells.
/// </summary>
/// <param name="Assignments">Prefix assignments in source order.</param>
/// <param name="Words">Unexpanded words in source order.</param>
public sealed record ShellSimpleCommand(IReadOnlyList<ShellAssignment> Assignments, IReadOnlyList<ShellWord> Words) : ShellCommand;

/// <summary><c>{ list; }</c> (run by the current shell) or <c>( list )</c> (run by a subshell copy).</summary>
/// <param name="Body">The commands of the group.</param>
/// <param name="Subshell"><see langword="true"/> for parentheses.</param>
public sealed record ShellGroupCommand(ShellCommandList Body, bool Subshell) : ShellCommand;

/// <summary>One <c>if</c>/<c>elif</c> branch: its condition and its body.</summary>
public sealed record ShellIfBranch(ShellCommandList Condition, ShellCommandList Body);

/// <summary><c>if c; then b; elif c; then b; else b; fi</c>.</summary>
/// <param name="Branches">The <c>if</c> branch then each <c>elif</c> branch.</param>
/// <param name="Else">The <c>else</c> body, if any.</param>
public sealed record ShellIfCommand(IReadOnlyList<ShellIfBranch> Branches, ShellCommandList? Else) : ShellCommand;

/// <summary><c>while c; do b; done</c> or <c>until c; do b; done</c>.</summary>
public sealed record ShellLoopCommand(ShellCommandList Condition, ShellCommandList Body, bool Until) : ShellCommand;

/// <summary><c>for NAME [in words]; do body; done</c>; without <c>in</c> the loop walks <c>"$@"</c>.</summary>
public sealed record ShellForCommand(string Variable, IReadOnlyList<ShellWord>? Items, ShellCommandList Body) : ShellCommand;

/// <summary>One <c>pattern | pattern) body ;;</c> item of a <c>case</c>.</summary>
public sealed record ShellCaseItem(IReadOnlyList<ShellWord> Patterns, ShellCommandList? Body);

/// <summary><c>case word in items esac</c>.</summary>
public sealed record ShellCaseCommand(ShellWord Subject, IReadOnlyList<ShellCaseItem> Items) : ShellCommand;

/// <summary><c>name() compound-command</c> or bash's <c>function name { … }</c>.</summary>
public sealed record ShellFunctionDefinition(string Name, ShellCommand Body) : ShellCommand;

/// <summary>Kind of a redirection.</summary>
public enum ShellRedirectionKind
{
    /// <summary><c>[n]&gt;</c> (and <c>&gt;|</c>): creates the file or empties it.</summary>
    Truncate,

    /// <summary><c>[n]&gt;&gt;</c>: creates the file or appends to its end.</summary>
    Append,

    /// <summary><c>[n]&lt;</c>: reads the file.</summary>
    Input,

    /// <summary><c>[n]&gt;&amp;m</c>: descriptor <c>n</c> (1 by default) becomes a copy of <c>m</c> (<c>-</c> closes it).</summary>
    DuplicateOutput,

    /// <summary><c>[n]&lt;&amp;m</c>: descriptor <c>n</c> (0 by default) becomes a copy of <c>m</c>.</summary>
    DuplicateInput,

    /// <summary><c>&lt;&lt;</c> or <c>&lt;&lt;-</c>: the target is the here-document's body.</summary>
    HereDocument
}

/// <summary>One redirection of a command.</summary>
/// <param name="Kind">What the redirection does.</param>
/// <param name="Target">Unexpanded target word (the file, the descriptor number, or the here-document body).</param>
/// <param name="Descriptor">The descriptor redirected (<c>2</c> in <c>2&gt;f</c>); <see langword="null"/> for the default (0 for input, 1 for output).</param>
public sealed record ShellRedirection(ShellRedirectionKind Kind, ShellWord Target, int? Descriptor = null)
{
    /// <summary>Gets the descriptor actually redirected.</summary>
    public int EffectiveDescriptor =>
        Descriptor ?? (Kind is ShellRedirectionKind.Input or ShellRedirectionKind.DuplicateInput or ShellRedirectionKind.HereDocument ? 0 : 1);
}

/// <summary>
/// One unexpanded word. It keeps how each piece was quoted, so the expansion step knows what to
/// substitute, split and glob without ever re-reading the result as shell syntax.
/// </summary>
/// <param name="Parts">Pieces of the word in source order.</param>
/// <param name="Offset">Offset of the word's first character in the source.</param>
/// <param name="Length">Number of source characters the word spans.</param>
public sealed record ShellWord(IReadOnlyList<ShellWordPart> Parts, int Offset, int Length)
{
    /// <summary>
    /// Gets the word's text when it is made only of unquoted, unescaped literal characters (the
    /// only words a shell considers for reserved words, alias expansion and function names);
    /// otherwise <see langword="null"/>.
    /// </summary>
    public string? PlainText =>
        Parts.Count == 1 && Parts[0] is ShellLiteralPart { Quoted: false } literal ? literal.Text : null;

    /// <summary>Gets whether any piece of the word was quoted or escaped (a quoted here-document delimiter, for one).</summary>
    public bool HasQuoting => Parts.Any(part => part is ShellLiteralPart { Quoted: true } || part.IsQuoted);

    /// <summary>Gets the word's source-independent literal text, ignoring expansions (used for here-document delimiters).</summary>
    public string LiteralText => string.Concat(Parts.OfType<ShellLiteralPart>().Select(part => part.Text));
}

/// <summary>One piece of a <see cref="ShellWord"/>.</summary>
public abstract record ShellWordPart
{
    /// <summary>Gets whether the piece came from inside double quotes: its expansion is then neither split nor globbed.</summary>
    public virtual bool IsQuoted => false;
}

/// <summary>Literal text.</summary>
/// <param name="Text">The characters, quotes and escapes already removed.</param>
/// <param name="Quoted">
/// <see langword="true"/> when the text came from quotes or a backslash escape, so it can never be
/// taken for an assignment, a reserved word, an alias name, a tilde or a glob pattern.
/// </param>
public sealed record ShellLiteralPart(string Text, bool Quoted) : ShellWordPart
{
    /// <inheritdoc />
    public override bool IsQuoted => Quoted;
}

/// <summary><c>$NAME</c> or <c>${NAME}</c>: the variable's value, or an empty string when it is not set.</summary>
/// <param name="Name">Variable name, matching <c>[A-Za-z_][A-Za-z0-9_]*</c>.</param>
/// <param name="Quoted">Whether it appeared inside double quotes.</param>
public sealed record ShellVariablePart(string Name, bool Quoted = false) : ShellWordPart
{
    /// <inheritdoc />
    public override bool IsQuoted => Quoted;
}

/// <summary><c>$?</c>: the exit code of the last pipeline this shell ran.</summary>
/// <param name="Quoted">Whether it appeared inside double quotes.</param>
public sealed record ShellExitStatusPart(bool Quoted = false) : ShellWordPart
{
    /// <inheritdoc />
    public override bool IsQuoted => Quoted;
}

/// <summary>
/// A special or positional parameter: <c>$0</c>…<c>$9</c>, <c>${10}</c>, <c>$#</c>, <c>$@</c>,
/// <c>$*</c>, <c>$$</c>, <c>$!</c>, <c>$-</c>.
/// </summary>
/// <param name="Name">The parameter: digits, or one of <c># @ * $ ! -</c>.</param>
/// <param name="Quoted">Whether it appeared inside double quotes (<c>"$@"</c> gives one field per parameter).</param>
public sealed record ShellSpecialParameterPart(string Name, bool Quoted = false) : ShellWordPart
{
    /// <inheritdoc />
    public override bool IsQuoted => Quoted;
}

/// <summary>
/// <c>${NAME op word}</c>: <c>:-</c>, <c>-</c>, <c>:=</c>, <c>=</c>, <c>:+</c>, <c>+</c>, <c>:?</c>,
/// <c>?</c>, pattern removal <c>#</c>, <c>##</c>, <c>%</c>, <c>%%</c>, and the length <c>${#NAME}</c>
/// (operator <c>length</c>, no argument).
/// </summary>
/// <param name="Name">Variable, positional or special parameter.</param>
/// <param name="Operator">The operator as written, or <c>length</c>.</param>
/// <param name="Argument">The unexpanded word after the operator, if any.</param>
/// <param name="Quoted">Whether it appeared inside double quotes.</param>
public sealed record ShellParameterExpansionPart(string Name, string Operator, ShellWord? Argument, bool Quoted = false) : ShellWordPart
{
    /// <inheritdoc />
    public override bool IsQuoted => Quoted;
}

/// <summary><c>$(list)</c> or <c>`list`</c>: the standard output of the commands, trailing newlines removed.</summary>
/// <param name="Body">The commands to run in a subshell copy.</param>
/// <param name="Quoted">Whether it appeared inside double quotes.</param>
public sealed record ShellCommandSubstitutionPart(ShellCommandList? Body, bool Quoted = false) : ShellWordPart
{
    /// <inheritdoc />
    public override bool IsQuoted => Quoted;
}

/// <summary><c>$((expression))</c>: integer arithmetic, its operands expanded first.</summary>
/// <param name="Expression">The expression, with its <c>$</c> expansions kept as parts.</param>
/// <param name="Quoted">Whether it appeared inside double quotes.</param>
public sealed record ShellArithmeticPart(ShellWord Expression, bool Quoted = false) : ShellWordPart
{
    /// <inheritdoc />
    public override bool IsQuoted => Quoted;
}

/// <summary>An unquoted <c>~</c> at the start of a word (or of an assignment value), alone or before <c>/</c>: the value of <c>HOME</c>.</summary>
public sealed record ShellTildePart : ShellWordPart;

/// <summary>
/// A structured syntax error (ADR 0014, kept by ADR 0050 and 0064): a stable code, a message and
/// the offending source span. The parser returns it instead of throwing for user input.
/// </summary>
/// <param name="Code">Stable machine-readable code, one of <see cref="ShellSyntaxErrorCodes"/>.</param>
/// <param name="Message">Human-readable message, printed after <c>hsh: </c>.</param>
/// <param name="Offset">Offset of the first offending character.</param>
/// <param name="Length">Number of offending characters (at least 1, except at the end of the input).</param>
public sealed record ShellSyntaxError(string Code, string Message, int Offset, int Length)
{
    /// <summary>
    /// Gets the key of the message in <c>Resources/Strings.resx</c> (ADR 0104), or
    /// <see langword="null"/> when <see cref="Message"/> is only available in English.
    /// </summary>
    public string? TextKey { get; init; }

    /// <summary>Gets the arguments of the <see cref="TextKey"/> text (tokens, delimiters).</summary>
    public IReadOnlyList<object> TextArguments { get; init; } = [];

    /// <summary>
    /// Gets the message in the language of <paramref name="texts"/> (the shell's language, as bash
    /// translates its syntax errors through <c>gettext</c>); <see cref="Message"/> stays English.
    /// </summary>
    /// <param name="texts">The core's texts in the shell's language (<c>Strings.For(culture)</c>).</param>
    public string Localize(Microsoft.Extensions.Localization.IStringLocalizer texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return TextKey is null ? Message : texts[TextKey, [.. TextArguments]];
    }

    /// <summary>Creates an error whose English <see cref="Message"/> comes from its resource text.</summary>
    /// <param name="code">One of <see cref="ShellSyntaxErrorCodes"/>.</param>
    /// <param name="offset">Offset of the first offending character.</param>
    /// <param name="length">Number of offending characters.</param>
    /// <param name="textKey">Key of the message in <c>Resources/Strings.resx</c>.</param>
    /// <param name="arguments">Arguments of the message.</param>
    internal static ShellSyntaxError Create(string code, int offset, int length, string textKey, params object[] arguments) =>
        new(code, ShellText.English[textKey, arguments], offset, length) { TextKey = textKey, TextArguments = arguments };
}

/// <summary>Stable codes of <see cref="ShellSyntaxError"/>.</summary>
public static class ShellSyntaxErrorCodes
{
    /// <summary>An operator the shell does not implement (<c>&lt;&gt;</c>, <c>&lt;&lt;&lt;</c>, <c>select</c>…).</summary>
    public const string Unsupported = "shell.syntax.unsupported";

    /// <summary>An operator or reserved word where it cannot appear, or two operators in a row.</summary>
    public const string UnexpectedToken = "shell.syntax.unexpected-token";

    /// <summary>The input ended inside a compound command, a quote or after an operator that needs more (<c>if</c> without <c>fi</c>, a trailing <c>|</c>).</summary>
    public const string UnexpectedEnd = "shell.syntax.unexpected-end";

    /// <summary>A redirection operator with no word after it.</summary>
    public const string MissingRedirectTarget = "shell.syntax.missing-redirect-target";

    /// <summary>A single or double quote that is never closed.</summary>
    public const string UnterminatedQuote = "shell.syntax.unterminated-quote";

    /// <summary>A backslash as the last character of the input.</summary>
    public const string TrailingEscape = "shell.syntax.trailing-escape";

    /// <summary>A <c>${</c> with no closing brace or with an invalid name inside.</summary>
    public const string BadSubstitution = "shell.syntax.bad-substitution";

    /// <summary>A <c>$(</c>, <c>`</c> or <c>$((</c> that is never closed.</summary>
    public const string UnterminatedSubstitution = "shell.syntax.unterminated-substitution";

    /// <summary>A here-document whose delimiter line never comes.</summary>
    public const string UnterminatedHereDocument = "shell.syntax.unterminated-here-document";

    /// <summary>A function name that is not a valid name.</summary>
    public const string BadFunctionName = "shell.syntax.bad-function-name";
}

/// <summary>Outcome of <see cref="ShellParser.Parse(string)"/>.</summary>
/// <param name="Commands">The parsed program, or <see langword="null"/> on error or for blank input.</param>
/// <param name="Error">The syntax error, or <see langword="null"/> on success.</param>
public sealed record ShellParseResult(ShellCommandList? Commands, ShellSyntaxError? Error)
{
    /// <summary>Gets whether the input parsed without error (blank input included).</summary>
    public bool IsSuccess => Error is null;

    /// <summary>Gets whether the input is blank (or only comments): nothing to run, no history entry.</summary>
    public bool IsEmpty => Error is null && Commands is null;

    /// <summary>
    /// Gets whether the input is only incomplete (an open quote, an <c>if</c> without <c>fi</c>, a
    /// trailing <c>|</c>…): a terminal can then ask for a continuation line, like bash's <c>PS2</c>.
    /// </summary>
    public bool IsIncomplete => Error?.Code is ShellSyntaxErrorCodes.UnexpectedEnd
        or ShellSyntaxErrorCodes.UnterminatedQuote
        or ShellSyntaxErrorCodes.UnterminatedSubstitution
        or ShellSyntaxErrorCodes.UnterminatedHereDocument
        or ShellSyntaxErrorCodes.TrailingEscape;
}
