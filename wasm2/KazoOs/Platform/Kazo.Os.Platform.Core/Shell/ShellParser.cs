using System.Text;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Parses a command line or a whole shell script into a <see cref="ShellCommandList"/> (ADR 0064,
/// a POSIX <c>sh</c> subset superseding ADR 0050 section 2), or a structured
/// <see cref="ShellSyntaxError"/>. Never throws for user input.
/// </summary>
/// <remarks>
/// <para>
/// Grammar (POSIX names): <c>program := list</c>; <c>list := and_or ((";" | "&amp;" | newline) and_or)*</c>;
/// <c>and_or := pipeline (("&amp;&amp;" | "||") pipeline)*</c>; <c>pipeline := ["!"] command ("|" command)*</c>;
/// <c>command := simple_command | compound_command redirect* | function_definition</c>;
/// compound commands are <c>{ }</c>, <c>( )</c>, <c>if</c>, <c>while</c>, <c>until</c>, <c>for</c>
/// and <c>case</c>. Redirections: <c>[n]&gt;</c>, <c>[n]&gt;&gt;</c>, <c>[n]&lt;</c>,
/// <c>[n]&gt;&amp;m</c>, <c>[n]&lt;&amp;m</c>, <c>&amp;&gt;</c>, <c>&amp;&gt;&gt;</c>,
/// <c>&lt;&lt;</c> and <c>&lt;&lt;-</c>.
/// </para>
/// <para>
/// Words, quotes and escapes keep ADR 0014: single quotes are literal; double quotes keep
/// whitespace, expand parameters, commands and arithmetic, and understand <c>\"</c>, <c>\\</c>,
/// <c>\$</c>, <c>\`</c>, <c>\n</c>, <c>\r</c> and <c>\t</c>; outside quotes a backslash makes the
/// next character literal, and a backslash-newline joins two lines. <c>#</c> at the start of a
/// word starts a comment.
/// </para>
/// <para>
/// The parser runs in two steps, like a real shell: a lexer cuts the input into words and
/// operators (reading here-document bodies at each newline), then a recursive-descent parser
/// checks the tokens against the grammar. Expansions are only recorded here;
/// <see cref="ShellExpander"/> performs them right before a command runs.
/// </para>
/// </remarks>
public static class ShellParser
{
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "if", "then", "elif", "else", "fi", "do", "done", "case", "esac", "while", "until", "for", "in", "{", "}", "!", "function"
    };

    /// <summary>Parses <paramref name="source"/>.</summary>
    /// <param name="source">One submitted line, or a script's whole text.</param>
    /// <returns>The tree, a blank result, or the first syntax error.</returns>
    public static ShellParseResult Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Parse(source, 0);
    }

    /// <summary>Parses <paramref name="source"/>, reporting offsets shifted by <paramref name="baseOffset"/> (command substitutions).</summary>
    internal static ShellParseResult Parse(string source, int baseOffset)
    {
        Lexer lexer = new(source, baseOffset, LexMode.Normal, quotedContext: false);
        if (!lexer.Run(out ShellSyntaxError? lexError))
        {
            return new ShellParseResult(null, lexError);
        }

        Parser parser = new(lexer.Tokens, baseOffset + source.Length);
        return parser.ParseProgram();
    }

    /// <summary>
    /// Parses text as the inside of a here-document or an arithmetic expression: only <c>$</c>,
    /// <c>`</c> and the backslash before <c>$ ` \</c> or a newline are special.
    /// </summary>
    internal static ShellWord ParseHereDocumentText(string text, int baseOffset, out ShellSyntaxError? error)
    {
        Lexer lexer = new(text, baseOffset, LexMode.HereDocument, quotedContext: true);
        return lexer.ReadWholeWord(out error);
    }

    /// <summary>Gets whether <paramref name="text"/> is a valid variable name.</summary>
    internal static bool IsName(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty || !IsNameStart(text[0]))
        {
            return false;
        }

        foreach (char ch in text[1..])
        {
            if (!IsNameChar(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNameStart(char ch) => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    private static bool IsNameChar(char ch) => IsNameStart(ch) || ch is >= '0' and <= '9';

    private enum LexMode
    {
        /// <summary>A command line or script: words, operators, comments, newlines.</summary>
        Normal,

        /// <summary>The argument of <c>${x:-word}</c>: one word, blanks and operators literal, quotes processed.</summary>
        Fragment,

        /// <summary>A here-document body or an arithmetic expression: only expansions and a few escapes.</summary>
        HereDocument
    }

    private enum TokenKind
    {
        Word,
        Operator,
        Newline,
        End
    }

    /// <summary>A here-document whose body is read at the next newline.</summary>
    private sealed class HereDocument(ShellWord delimiter, bool stripTabs)
    {
        public ShellWord Delimiter { get; } = delimiter;

        public bool StripTabs { get; } = stripTabs;

        public ShellWord? Body { get; set; }
    }

    private sealed record Token(TokenKind Kind, string Text, int Offset, int Length, ShellWord? Word = null, int? IoNumber = null)
    {
        public HereDocument? HereDocument { get; set; }

        public bool IsOperator(string text) => Kind == TokenKind.Operator && Text == text;

        public bool IsReserved(string word) => Kind == TokenKind.Word && Word!.PlainText == word;
    }

    // ---- Lexer ---------------------------------------------------------------------------

    /// <summary>Cuts input into words and operators, recording quoting per word piece.</summary>
    private sealed class Lexer(string text, int baseOffset, LexMode mode, bool quotedContext)
    {
        private readonly List<ShellWordPart> _parts = [];
        private readonly StringBuilder _literal = new();
        private readonly List<HereDocument> _pendingHereDocuments = [];
        private bool _literalQuoted;
        private bool _inWord;
        private int _wordStart;
        private int _position;
        private bool _expectHereDelimiter;
        private bool _hereStripTabs;
        private Token? _hereOperator;

        public List<Token> Tokens { get; } = [];

        private char Peek(int ahead = 0) => _position + ahead < text.Length ? text[_position + ahead] : '\0';

        public bool Run(out ShellSyntaxError? error)
        {
            error = null;
            while (_position < text.Length)
            {
                char ch = text[_position];
                if (ch == '\n')
                {
                    EndWord();
                    Tokens.Add(new Token(TokenKind.Newline, "\n", baseOffset + _position, 1));
                    _position++;
                    if (_pendingHereDocuments.Count > 0 && (error = ReadHereDocumentBodies()) is not null)
                    {
                        return false;
                    }

                    continue;
                }

                if (ch is ' ' or '\t' or '\r')
                {
                    EndWord();
                    _position++;
                    continue;
                }

                if (ch == '#' && !_inWord)
                {
                    while (_position < text.Length && text[_position] != '\n')
                    {
                        _position++;
                    }

                    continue;
                }

                error = ch switch
                {
                    '\'' => ReadSingleQuoted(),
                    '"' => ReadDoubleQuoted(),
                    '\\' => ReadEscape(),
                    '$' => ReadDollar(quoted: false),
                    '`' => ReadBackquote(quoted: false),
                    '~' when !_inWord => ReadTilde(),
                    '|' or '&' or '>' or ';' or '<' or '(' or ')' => ReadOperator(ch),
                    _ => AppendLiteral(ch, quoted: false)
                };

                if (error is not null)
                {
                    return false;
                }
            }

            EndWord();
            if (_expectHereDelimiter)
            {
                error = ShellSyntaxError.Create(
                    ShellSyntaxErrorCodes.MissingRedirectTarget,
                    baseOffset + text.Length,
                    0,
                    "Syntax.MissingHereDocumentDelimiter",
                    _hereOperator!.Text);
                return false;
            }

            if (_pendingHereDocuments.Count > 0)
            {
                // A here-document needs the lines after its command: on one terminal line the
                // input is incomplete, like bash waiting at its `>` prompt.
                HereDocument first = _pendingHereDocuments[0];
                error = ShellSyntaxError.Create(
                    ShellSyntaxErrorCodes.UnterminatedHereDocument,
                    first.Delimiter.Offset,
                    first.Delimiter.Length,
                    "Syntax.HereDocumentNotTerminated",
                    first.Delimiter.LiteralText);
                return false;
            }

            Tokens.Add(new Token(TokenKind.End, string.Empty, baseOffset + text.Length, 0));
            return true;
        }

        /// <summary>Reads the whole input as one word (fragment and here-document modes).</summary>
        public ShellWord ReadWholeWord(out ShellSyntaxError? error)
        {
            error = null;
            _inWord = true;
            _wordStart = 0;
            while (_position < text.Length && error is null)
            {
                char ch = text[_position];
                if (mode == LexMode.HereDocument)
                {
                    error = ch switch
                    {
                        '$' => ReadDollar(quoted: true),
                        '`' => ReadBackquote(quoted: true),
                        '\\' => ReadHereDocumentEscape(),
                        _ => AppendLiteral(ch, quoted: true)
                    };
                    continue;
                }

                error = ch switch
                {
                    '\'' when !quotedContext => ReadSingleQuoted(),
                    '"' when !quotedContext => ReadDoubleQuoted(),
                    '\\' => ReadEscape(),
                    '$' => ReadDollar(quotedContext),
                    '`' => ReadBackquote(quotedContext),
                    '~' when _position == 0 && !quotedContext => ReadTilde(),
                    _ => AppendLiteral(ch, quotedContext)
                };
            }

            FlushLiteral();
            if (_parts.Count == 0)
            {
                _parts.Add(new ShellLiteralPart(string.Empty, Quoted: true));
            }

            return new ShellWord([.. _parts], baseOffset, text.Length);
        }

        private ShellSyntaxError? ReadHereDocumentEscape()
        {
            char next = Peek(1);
            switch (next)
            {
                case '$' or '`' or '\\':
                    return AppendLiteral(next, quoted: true, advance: 2);
                case '\n':
                    _position += 2;
                    return null;
                default:
                    return AppendLiteral('\\', quoted: true);
            }
        }

        private ShellSyntaxError? AppendLiteral(char ch, bool quoted, int advance = 1)
        {
            StartWord();
            if (_literal.Length > 0 && _literalQuoted != quoted)
            {
                FlushLiteral();
            }

            _literalQuoted = quoted;
            _literal.Append(ch);
            _position += advance;
            return null;
        }

        private ShellSyntaxError? ReadSingleQuoted()
        {
            int start = _position;
            StartWord();
            int end = text.IndexOf('\'', start + 1);
            if (end < 0)
            {
                return ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnterminatedQuote, baseOffset + start, text.Length - start, "Syntax.UnterminatedSingleQuote");
            }

            for (int i = start + 1; i < end; i++)
            {
                AppendQuotedChar(text[i]);
            }

            if (end == start + 1)
            {
                // '' still makes a (quoted) word.
                AppendQuotedEmpty();
            }

            _position = end + 1;
            return null;
        }

        private ShellSyntaxError? ReadDoubleQuoted()
        {
            int start = _position;
            StartWord();
            _position++;
            bool any = false;
            while (_position < text.Length)
            {
                char ch = text[_position];
                if (ch == '"')
                {
                    _position++;
                    if (!any)
                    {
                        AppendQuotedEmpty();
                    }

                    return null;
                }

                any = true;
                if (ch == '\\' && _position + 1 < text.Length)
                {
                    char next = text[_position + 1];
                    if (next == '\n')
                    {
                        _position += 2;
                        continue;
                    }

                    char? escaped = next switch
                    {
                        '"' or '\\' or '$' or '`' => next,
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => null
                    };
                    if (escaped is { } value)
                    {
                        AppendQuotedChar(value);
                        _position += 2;
                    }
                    else
                    {
                        // Unknown escapes keep their backslash, as inside POSIX double quotes.
                        AppendQuotedChar('\\');
                        _position++;
                    }

                    continue;
                }

                ShellSyntaxError? error = ch switch
                {
                    '$' => ReadDollar(quoted: true),
                    '`' => ReadBackquote(quoted: true),
                    _ => null
                };
                if (error is not null)
                {
                    return error;
                }

                if (ch is not ('$' or '`'))
                {
                    AppendQuotedChar(ch);
                    _position++;
                }
            }

            return ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnterminatedQuote, baseOffset + start, text.Length - start, "Syntax.UnterminatedDoubleQuote");
        }

        private ShellSyntaxError? ReadEscape()
        {
            if (_position + 1 >= text.Length)
            {
                return ShellSyntaxError.Create(ShellSyntaxErrorCodes.TrailingEscape, baseOffset + _position, 1, "Syntax.TrailingEscape");
            }

            if (text[_position + 1] == '\n')
            {
                // Line continuation: both characters disappear.
                _position += 2;
                return null;
            }

            return AppendLiteral(text[_position + 1], quoted: true, advance: 2);
        }

        /// <summary>Reads every <c>$</c> form; a <c>$</c> before anything else is literal.</summary>
        private ShellSyntaxError? ReadDollar(bool quoted)
        {
            int start = _position;
            char next = Peek(1);
            if (next == '(' && Peek(2) == '(')
            {
                return ReadArithmetic(quoted);
            }

            if (next == '(')
            {
                int close = FindClosingParenthesis(start + 2);
                if (close < 0)
                {
                    return ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnterminatedSubstitution, baseOffset + start, text.Length - start, "Syntax.UnterminatedCommandSubstitution");
                }

                return AddSubstitution(text[(start + 2)..close], start + 2, close + 1, quoted);
            }

            if (next == '{')
            {
                return ReadBracedParameter(quoted);
            }

            if (next == '?')
            {
                AddPart(new ShellExitStatusPart(quoted));
                _position += 2;
                return null;
            }

            if (next is '#' or '@' or '*' or '$' or '!' or '-' || char.IsAsciiDigit(next))
            {
                AddPart(new ShellSpecialParameterPart(next.ToString(), quoted));
                _position += 2;
                return null;
            }

            if (IsNameStart(next))
            {
                int end = _position + 1;
                while (end < text.Length && IsNameChar(text[end]))
                {
                    end++;
                }

                AddPart(new ShellVariablePart(text[(_position + 1)..end], quoted));
                _position = end;
                return null;
            }

            return quoted ? AppendQuotedLiteralAndAdvance('$') : AppendLiteral('$', quoted: false);
        }

        private ShellSyntaxError? AppendQuotedLiteralAndAdvance(char ch)
        {
            AppendQuotedChar(ch);
            _position++;
            return null;
        }

        /// <summary><c>${NAME}</c>, <c>${#NAME}</c>, <c>${NAME op word}</c>.</summary>
        private ShellSyntaxError? ReadBracedParameter(bool quoted)
        {
            int start = _position;
            int close = FindClosingBrace(start + 2);
            if (close < 0)
            {
                return BadSubstitution(start, text.Length - start);
            }

            string inside = text[(start + 2)..close];
            ShellWordPart? part = null;
            if (inside.Length > 1 && inside[0] == '#' && IsParameterName(inside[1..]))
            {
                part = new ShellParameterExpansionPart(inside[1..], "length", null, quoted);
            }
            else
            {
                int nameLength = ParameterNameLength(inside);
                if (nameLength == 0)
                {
                    return BadSubstitution(start, close - start + 1);
                }

                string name = inside[..nameLength];
                string rest = inside[nameLength..];
                if (rest.Length == 0)
                {
                    part = name switch
                    {
                        "?" => new ShellExitStatusPart(quoted),
                        _ when IsName(name) => new ShellVariablePart(name, quoted),
                        _ => new ShellSpecialParameterPart(name, quoted)
                    };
                }
                else
                {
                    string? op = rest.Length >= 2 && rest[0] == ':' && rest[1] is '-' or '=' or '+' or '?' ? rest[..2]
                        : rest.StartsWith("##", StringComparison.Ordinal) || rest.StartsWith("%%", StringComparison.Ordinal) ? rest[..2]
                        : rest[0] is '-' or '=' or '+' or '?' or '#' or '%' ? rest[..1]
                        : null;
                    if (op is null)
                    {
                        return BadSubstitution(start, close - start + 1);
                    }

                    string argumentText = rest[op.Length..];
                    int argumentOffset = start + 2 + nameLength + op.Length;
                    Lexer fragment = new(argumentText, baseOffset + argumentOffset, LexMode.Fragment, quoted);
                    ShellWord argument = fragment.ReadWholeWord(out ShellSyntaxError? error);
                    if (error is not null)
                    {
                        return error;
                    }

                    part = new ShellParameterExpansionPart(name, op, argument, quoted);
                }
            }

            AddPart(part);
            _position = close + 1;
            return null;
        }

        private ShellSyntaxError BadSubstitution(int start, int length) =>
            ShellSyntaxError.Create(ShellSyntaxErrorCodes.BadSubstitution, baseOffset + start, length, "Syntax.BadSubstitution", text.Substring(start, length));

        private static bool IsParameterName(string name) =>
            IsName(name) || name.All(char.IsAsciiDigit) || name is "#" or "@" or "*" or "$" or "!" or "-" or "?";

        /// <summary>Length of the parameter name at the start of a <c>${…}</c>: a name, digits or one special character.</summary>
        private static int ParameterNameLength(string inside)
        {
            if (inside.Length == 0)
            {
                return 0;
            }

            if (IsNameStart(inside[0]))
            {
                int length = 1;
                while (length < inside.Length && IsNameChar(inside[length]))
                {
                    length++;
                }

                return length;
            }

            if (char.IsAsciiDigit(inside[0]))
            {
                int length = 1;
                while (length < inside.Length && char.IsAsciiDigit(inside[length]))
                {
                    length++;
                }

                return length;
            }

            return inside[0] is '#' or '@' or '*' or '$' or '!' or '-' or '?' ? 1 : 0;
        }

        /// <summary><c>$((expression))</c>.</summary>
        private ShellSyntaxError? ReadArithmetic(bool quoted)
        {
            int start = _position;
            int depth = 0;
            for (int i = start + 3; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '(')
                {
                    depth++;
                }
                else if (ch == ')')
                {
                    if (depth == 0)
                    {
                        if (i + 1 < text.Length && text[i + 1] == ')')
                        {
                            ShellWord expression = ParseHereDocumentText(text[(start + 3)..i], baseOffset + start + 3, out ShellSyntaxError? error);
                            if (error is not null)
                            {
                                return error;
                            }

                            AddPart(new ShellArithmeticPart(expression, quoted));
                            _position = i + 2;
                            return null;
                        }

                        break;
                    }

                    depth--;
                }
            }

            // Not a well-formed $((…)): read it as $( (…) ), a command substitution of a subshell.
            int close = FindClosingParenthesis(start + 2);
            if (close < 0)
            {
                return ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnterminatedSubstitution, baseOffset + start, text.Length - start, "Syntax.UnterminatedArithmetic");
            }

            return AddSubstitution(text[(start + 2)..close], start + 2, close + 1, quoted);
        }

        /// <summary><c>`list`</c>: inside, <c>\$</c>, <c>\`</c> and <c>\\</c> lose their backslash, then the text is parsed.</summary>
        private ShellSyntaxError? ReadBackquote(bool quoted)
        {
            int start = _position;
            StringBuilder body = new();
            int i = start + 1;
            for (; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\\' && i + 1 < text.Length && text[i + 1] is '$' or '`' or '\\')
                {
                    body.Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (ch == '`')
                {
                    break;
                }

                body.Append(ch);
            }

            if (i >= text.Length)
            {
                return ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnterminatedSubstitution, baseOffset + start, text.Length - start, "Syntax.UnterminatedBackquote");
            }

            return AddSubstitution(body.ToString(), start + 1, i + 1, quoted);
        }

        private ShellSyntaxError? AddSubstitution(string body, int bodyOffset, int end, bool quoted)
        {
            ShellParseResult parsed = Parse(body, baseOffset + bodyOffset);
            if (parsed.Error is { } error)
            {
                return error;
            }

            AddPart(new ShellCommandSubstitutionPart(parsed.Commands, quoted));
            _position = end;
            return null;
        }

        /// <summary>Index of the <c>)</c> closing a <c>$(</c> whose body starts at <paramref name="from"/>, skipping quotes and nested parentheses.</summary>
        private int FindClosingParenthesis(int from)
        {
            int depth = 0;
            for (int i = from; i < text.Length; i++)
            {
                char ch = text[i];
                switch (ch)
                {
                    case '\\':
                        i++;
                        break;
                    case '\'':
                        int quote = text.IndexOf('\'', i + 1);
                        if (quote < 0)
                        {
                            return -1;
                        }

                        i = quote;
                        break;
                    case '"':
                        i = SkipDoubleQuoted(i);
                        if (i < 0)
                        {
                            return -1;
                        }

                        break;
                    case '`':
                        int back = text.IndexOf('`', i + 1);
                        if (back < 0)
                        {
                            return -1;
                        }

                        i = back;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        if (depth == 0)
                        {
                            return i;
                        }

                        depth--;
                        break;
                }
            }

            return -1;
        }

        /// <summary>Index of the closing <c>"</c> of a double-quoted string starting at <paramref name="open"/>.</summary>
        private int SkipDoubleQuoted(int open)
        {
            for (int i = open + 1; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    return i;
                }
                else if (ch == '$' && i + 1 < text.Length && text[i + 1] == '(')
                {
                    int close = FindClosingParenthesis(i + 2);
                    if (close < 0)
                    {
                        return -1;
                    }

                    i = close;
                }
            }

            return -1;
        }

        /// <summary>Index of the <c>}</c> closing a <c>${</c>, skipping quotes and nested braces.</summary>
        private int FindClosingBrace(int from)
        {
            int depth = 0;
            for (int i = from; i < text.Length; i++)
            {
                char ch = text[i];
                switch (ch)
                {
                    case '\\':
                        i++;
                        break;
                    case '\'':
                        int quote = text.IndexOf('\'', i + 1);
                        if (quote < 0)
                        {
                            return -1;
                        }

                        i = quote;
                        break;
                    case '"':
                        i = SkipDoubleQuoted(i);
                        if (i < 0)
                        {
                            return -1;
                        }

                        break;
                    case '{':
                        depth++;
                        break;
                    case '}':
                        if (depth == 0)
                        {
                            return i;
                        }

                        depth--;
                        break;
                }
            }

            return -1;
        }

        /// <summary>An unquoted <c>~</c> opening a word is a tilde only before the end of the word or <c>/</c>.</summary>
        private ShellSyntaxError? ReadTilde()
        {
            char next = Peek(1);
            if (next is '\0' or ' ' or '\t' or '\n' or '/' or '|' or '&' or '>' or ';' or '<' or ')' or ':')
            {
                AddPart(new ShellTildePart());
                _position++;
                return null;
            }

            return AppendLiteral('~', quoted: false);
        }

        private ShellSyntaxError? ReadOperator(char ch)
        {
            char next = Peek(1);
            char third = Peek(2);
            string op = ch switch
            {
                '|' when next == '|' => "||",
                '|' => "|",
                '&' when next == '&' => "&&",
                '&' when next == '>' && third == '>' => "&>>",
                '&' when next == '>' => "&>",
                '&' => "&",
                ';' when next == ';' => ";;",
                ';' => ";",
                '>' when next == '>' => ">>",
                '>' when next == '&' => ">&",
                '>' when next == '|' => ">|",
                '>' => ">",
                '<' when next == '<' && third == '-' => "<<-",
                '<' when next == '<' && third == '<' => "<<<",
                '<' when next == '<' => "<<",
                '<' when next == '&' => "<&",
                '<' when next == '>' => "<>",
                '<' => "<",
                _ => ch.ToString()
            };

            if (op is "<<<" or "<>")
            {
                return ShellSyntaxError.Create(
                    ShellSyntaxErrorCodes.Unsupported,
                    baseOffset + _position,
                    op.Length,
                    "Syntax.Unsupported",
                    op);
            }

            int? ioNumber = null;
            if (op[0] is '<' or '>' && TryTakeDescriptorPrefix(out int descriptor))
            {
                ioNumber = descriptor;
            }

            EndWord();
            Token token = new(TokenKind.Operator, op, baseOffset + _position, op.Length, IoNumber: ioNumber);
            Tokens.Add(token);
            _position += op.Length;
            if (op is "<<" or "<<-")
            {
                _expectHereDelimiter = true;
                _hereStripTabs = op == "<<-";
                _hereOperator = token;
            }

            return null;
        }

        /// <summary>
        /// Takes the word being read as a descriptor number when it is made only of unquoted digits
        /// directly before a redirection operator (<c>2</c> in <c>2&gt;</c>).
        /// </summary>
        private bool TryTakeDescriptorPrefix(out int descriptor)
        {
            descriptor = 0;
            if (!_inWord || _parts.Count != 0 || _literalQuoted || _literal.Length is 0 or > 2)
            {
                return false;
            }

            string digits = _literal.ToString();
            if (!digits.All(char.IsAsciiDigit))
            {
                return false;
            }

            descriptor = int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
            _literal.Clear();
            _inWord = false;
            return true;
        }

        /// <summary>Reads the bodies of the here-documents opened on the line that just ended.</summary>
        private ShellSyntaxError? ReadHereDocumentBodies()
        {
            foreach (HereDocument document in _pendingHereDocuments)
            {
                string delimiter = document.Delimiter.LiteralText;
                StringBuilder body = new();
                int bodyStart = _position;
                bool terminated = false;
                while (_position < text.Length)
                {
                    int lineEnd = text.IndexOf('\n', _position);
                    string line = lineEnd < 0 ? text[_position..] : text[_position..lineEnd];
                    _position = lineEnd < 0 ? text.Length : lineEnd + 1;
                    if (document.StripTabs)
                    {
                        line = line.TrimStart('\t');
                    }

                    if (line.TrimEnd('\r') == delimiter)
                    {
                        terminated = true;
                        break;
                    }

                    body.Append(line).Append('\n');
                }

                if (!terminated)
                {
                    return ShellSyntaxError.Create(
                        ShellSyntaxErrorCodes.UnterminatedHereDocument,
                        document.Delimiter.Offset,
                        document.Delimiter.Length,
                        "Syntax.HereDocumentNotTerminated",
                        delimiter);
                }

                if (document.Delimiter.HasQuoting)
                {
                    // A quoted delimiter makes the body entirely literal.
                    document.Body = new ShellWord([new ShellLiteralPart(body.ToString(), Quoted: true)], baseOffset + bodyStart, body.Length);
                }
                else
                {
                    document.Body = ParseHereDocumentText(body.ToString(), baseOffset + bodyStart, out ShellSyntaxError? error);
                    if (error is not null)
                    {
                        return error;
                    }
                }
            }

            _pendingHereDocuments.Clear();
            return null;
        }

        private void AppendQuotedChar(char ch)
        {
            if (_literal.Length > 0 && !_literalQuoted)
            {
                FlushLiteral();
            }

            _literalQuoted = true;
            _literal.Append(ch);
        }

        /// <summary>Records that the word contains quotes even when they hold nothing (<c>""</c>, <c>''</c>).</summary>
        private void AppendQuotedEmpty()
        {
            FlushLiteral();
            _parts.Add(new ShellLiteralPart(string.Empty, Quoted: true));
        }

        private void AddPart(ShellWordPart part)
        {
            StartWord();
            FlushLiteral();
            _parts.Add(part);
        }

        private void StartWord()
        {
            if (!_inWord)
            {
                _inWord = true;
                _wordStart = _position;
            }
        }

        private void FlushLiteral()
        {
            if (_literal.Length > 0)
            {
                _parts.Add(new ShellLiteralPart(_literal.ToString(), _literalQuoted));
                _literal.Clear();
            }
        }

        private void EndWord()
        {
            if (!_inWord || mode != LexMode.Normal)
            {
                return;
            }

            FlushLiteral();
            List<ShellWordPart> parts = MergeParts(_parts);
            if (parts.Count == 0)
            {
                parts.Add(new ShellLiteralPart(string.Empty, Quoted: true));
            }

            ShellWord word = new(parts, baseOffset + _wordStart, _position - _wordStart);
            Tokens.Add(new Token(TokenKind.Word, text.Substring(_wordStart, _position - _wordStart), baseOffset + _wordStart, _position - _wordStart, word));
            _parts.Clear();
            _inWord = false;

            if (_expectHereDelimiter)
            {
                _expectHereDelimiter = false;
                HereDocument document = new(word, _hereStripTabs);
                _hereOperator!.HereDocument = document;
                _pendingHereDocuments.Add(document);
            }
        }

        /// <summary>Joins adjacent literal pieces of the same quoting and drops empty quoted pieces next to text.</summary>
        private static List<ShellWordPart> MergeParts(List<ShellWordPart> parts)
        {
            List<ShellWordPart> merged = [];
            foreach (ShellWordPart part in parts)
            {
                if (part is ShellLiteralPart literal
                    && merged.Count > 0
                    && merged[^1] is ShellLiteralPart previous
                    && previous.Quoted == literal.Quoted)
                {
                    merged[^1] = new ShellLiteralPart(previous.Text + literal.Text, literal.Quoted);
                    continue;
                }

                merged.Add(part);
            }

            // An empty quoted piece ("") only matters when nothing else in the word is literal text:
            // `""$x` with an empty x must still give one (empty) argument.
            if (merged.Count > 1 && merged.Any(part => part is ShellLiteralPart { Text.Length: > 0 }))
            {
                merged.RemoveAll(part => part is ShellLiteralPart { Text.Length: 0 });
            }

            return merged;
        }
    }

    // ---- Parser --------------------------------------------------------------------------

    /// <summary>Recursive descent over the token list.</summary>
    private sealed class Parser(List<Token> tokens, int endOffset)
    {
        private int _index;

        private Token Current => tokens[_index];

        public ShellParseResult ParseProgram()
        {
            try
            {
                SkipNewlines();
                if (Current.Kind == TokenKind.End)
                {
                    return new ShellParseResult(null, null);
                }

                ShellCommandList list = ParseList();
                if (Current.Kind != TokenKind.End)
                {
                    throw Unexpected(Current);
                }

                return new ShellParseResult(list, null);
            }
            catch (SyntaxException exception)
            {
                return new ShellParseResult(null, exception.Error);
            }
        }

        /// <summary>
        /// Parses and-or lists until a token that cannot start one: a closing reserved word,
        /// <c>)</c>, <c>;;</c> or the end of input.
        /// </summary>
        private ShellCommandList ParseList()
        {
            List<ShellListItem> items = [];
            SkipNewlines();
            while (!IsListTerminator(Current))
            {
                int start = Current.Offset;
                ShellAndOrList andOr = ParseAndOr();
                int end = tokens[_index - 1].Offset + tokens[_index - 1].Length;
                bool background = false;
                if (Current.IsOperator("&"))
                {
                    background = true;
                    _index++;
                }
                else if (Current.IsOperator(";"))
                {
                    _index++;
                }
                else if (Current.Kind == TokenKind.Newline)
                {
                    // handled by SkipNewlines below
                }
                else if (!IsListTerminator(Current))
                {
                    throw Unexpected(Current);
                }

                items.Add(new ShellListItem(andOr, background, start, end - start));
                SkipNewlines();
            }

            if (items.Count == 0)
            {
                throw Current.Kind == TokenKind.End
                    ? UnexpectedEnd()
                    : Unexpected(Current);
            }

            return new ShellCommandList(items);
        }

        private static bool IsListTerminator(Token token) =>
            token.Kind == TokenKind.End
            || token.IsOperator(")")
            || token.IsOperator(";;")
            || (token.Kind == TokenKind.Word && token.Word!.PlainText is "then" or "elif" or "else" or "fi" or "do" or "done" or "esac" or "}");

        private ShellAndOrList ParseAndOr()
        {
            List<ShellPipeline> pipelines = [ParsePipeline()];
            List<ShellAndOrOperator> operators = [];
            while (Current.IsOperator("&&") || Current.IsOperator("||"))
            {
                operators.Add(Current.Text == "&&" ? ShellAndOrOperator.And : ShellAndOrOperator.Or);
                _index++;
                SkipNewlines();
                pipelines.Add(ParsePipeline());
            }

            return new ShellAndOrList(pipelines, operators);
        }

        private ShellPipeline ParsePipeline()
        {
            bool negated = false;
            if (Current.IsReserved("!"))
            {
                negated = true;
                _index++;
            }

            List<ShellCommand> commands = [ParseCommand()];
            while (Current.IsOperator("|"))
            {
                _index++;
                SkipNewlines();
                commands.Add(ParseCommand());
            }

            return new ShellPipeline(commands, negated);
        }

        private ShellCommand ParseCommand()
        {
            Token token = Current;
            if (token.Kind == TokenKind.End)
            {
                throw UnexpectedEnd();
            }

            if (token.Kind == TokenKind.Newline)
            {
                throw Unexpected(token);
            }

            if (token.IsOperator("("))
            {
                _index++;
                ShellCommandList body = ParseList();
                Expect(")");
                return WithRedirections(new ShellGroupCommand(body, Subshell: true));
            }

            if (token.Kind == TokenKind.Word)
            {
                switch (token.Word!.PlainText)
                {
                    case "{":
                        _index++;
                        ShellCommandList group = ParseList();
                        ExpectReserved("}");
                        return WithRedirections(new ShellGroupCommand(group, Subshell: false));
                    case "if":
                        return WithRedirections(ParseIf());
                    case "while" or "until":
                        return WithRedirections(ParseLoop());
                    case "for":
                        return WithRedirections(ParseFor());
                    case "case":
                        return WithRedirections(ParseCase());
                    case "function":
                        return ParseFunctionKeyword();
                    case "then" or "elif" or "else" or "fi" or "do" or "done" or "esac" or "}" or "in":
                        throw Unexpected(token);
                }

                if (tokens[_index + 1].IsOperator("(") && tokens[_index + 2].IsOperator(")"))
                {
                    return ParseFunctionDefinition();
                }
            }

            if (token.Kind == TokenKind.Operator && !IsRedirectionOperator(token))
            {
                throw Unexpected(token);
            }

            return ParseSimpleCommand();
        }

        private ShellCommand ParseSimpleCommand()
        {
            List<ShellAssignment> assignments = [];
            List<ShellWord> words = [];
            List<ShellRedirection> redirections = [];
            while (true)
            {
                Token token = Current;
                if (token.Kind == TokenKind.Word)
                {
                    if (words.Count == 0 && TryReadAssignment(token.Word!) is { } assignment)
                    {
                        assignments.Add(assignment);
                    }
                    else
                    {
                        words.Add(token.Word!);
                    }

                    _index++;
                    continue;
                }

                if (IsRedirectionOperator(token))
                {
                    redirections.AddRange(ParseRedirection());
                    continue;
                }

                break;
            }

            if (assignments.Count == 0 && words.Count == 0 && redirections.Count == 0)
            {
                throw Current.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(Current);
            }

            return new ShellSimpleCommand(assignments, words) { Redirections = redirections };
        }

        /// <summary>Reads <c>NAME=value</c> when the word starts with an unquoted valid name followed by <c>=</c>.</summary>
        private static ShellAssignment? TryReadAssignment(ShellWord word)
        {
            if (word.Parts[0] is not ShellLiteralPart { Quoted: false } first)
            {
                return null;
            }

            int equals = first.Text.IndexOf('=');
            if (equals <= 0 || !IsName(first.Text.AsSpan(0, equals)))
            {
                return null;
            }

            List<ShellWordPart> value = [];
            string rest = first.Text[(equals + 1)..];
            if (rest.Length > 0)
            {
                value.Add(new ShellLiteralPart(rest, false));
            }

            value.AddRange(word.Parts.Skip(1));
            if (value.Count == 0)
            {
                value.Add(new ShellLiteralPart(string.Empty, true));
            }

            // `X=~/bin` expands the tilde, as at the start of a word.
            if (value[0] is ShellLiteralPart { Quoted: false } head && (head.Text == "~" || head.Text.StartsWith("~/", StringComparison.Ordinal)))
            {
                value[0] = new ShellTildePart();
                if (head.Text.Length > 1)
                {
                    value.Insert(1, new ShellLiteralPart(head.Text[1..], false));
                }
            }

            return new ShellAssignment(first.Text[..equals], new ShellWord(value, word.Offset + equals + 1, word.Length - equals - 1));
        }

        private static bool IsRedirectionOperator(Token token) =>
            token.Kind == TokenKind.Operator
            && token.Text is ">" or ">>" or ">|" or "<" or ">&" or "<&" or "&>" or "&>>" or "<<" or "<<-";

        private IEnumerable<ShellRedirection> ParseRedirection()
        {
            Token op = Current;
            _index++;
            Token target = Current;
            if (target.Kind != TokenKind.Word)
            {
                throw new SyntaxException(target.Kind is TokenKind.End or TokenKind.Newline
                    ? ShellSyntaxError.Create(ShellSyntaxErrorCodes.MissingRedirectTarget, target.Offset, target.Length, "Syntax.MissingFileName", op.Text)
                    : ShellSyntaxError.Create(ShellSyntaxErrorCodes.MissingRedirectTarget, target.Offset, target.Length, "Syntax.UnexpectedToken", target.Text));
            }

            _index++;
            ShellWord word = target.Word!;
            switch (op.Text)
            {
                case ">" or ">|":
                    return [new ShellRedirection(ShellRedirectionKind.Truncate, word, op.IoNumber)];
                case ">>":
                    return [new ShellRedirection(ShellRedirectionKind.Append, word, op.IoNumber)];
                case "<":
                    return [new ShellRedirection(ShellRedirectionKind.Input, word, op.IoNumber)];
                case "<&":
                    return [new ShellRedirection(ShellRedirectionKind.DuplicateInput, word, op.IoNumber)];
                case ">&":
                    // bash: `>&file` (no descriptor, a non-numeric word) is `&>file`.
                    if (op.IoNumber is null && word.PlainText is { } plain && !plain.All(char.IsAsciiDigit) && plain != "-")
                    {
                        return BothOutputs(ShellRedirectionKind.Truncate, word);
                    }

                    return [new ShellRedirection(ShellRedirectionKind.DuplicateOutput, word, op.IoNumber)];
                case "&>":
                    return BothOutputs(ShellRedirectionKind.Truncate, word);
                case "&>>":
                    return BothOutputs(ShellRedirectionKind.Append, word);
                default:
                    ShellWord body = op.HereDocument?.Body
                        ?? throw new SyntaxException(ShellSyntaxError.Create(
                            ShellSyntaxErrorCodes.UnterminatedHereDocument,
                            word.Offset,
                            word.Length,
                            "Syntax.HereDocumentBodyNotTerminated"));
                    return [new ShellRedirection(ShellRedirectionKind.HereDocument, body, op.IoNumber)];
            }
        }

        /// <summary><c>&amp;&gt;f</c> is <c>&gt;f 2&gt;&amp;1</c>.</summary>
        private static ShellRedirection[] BothOutputs(ShellRedirectionKind kind, ShellWord word) =>
        [
            new ShellRedirection(kind, word, 1),
            new ShellRedirection(ShellRedirectionKind.DuplicateOutput, new ShellWord([new ShellLiteralPart("1", false)], word.Offset, 0), 2)
        ];

        private ShellCommand WithRedirections(ShellCommand command)
        {
            List<ShellRedirection> redirections = [];
            while (IsRedirectionOperator(Current))
            {
                redirections.AddRange(ParseRedirection());
            }

            return redirections.Count == 0 ? command : command with { Redirections = redirections };
        }

        private ShellIfCommand ParseIf()
        {
            _index++; // if
            List<ShellIfBranch> branches = [];
            ShellCommandList condition = ParseList();
            ExpectReserved("then");
            ShellCommandList body = ParseList();
            branches.Add(new ShellIfBranch(condition, body));
            ShellCommandList? elseBody = null;
            while (true)
            {
                if (Current.IsReserved("elif"))
                {
                    _index++;
                    ShellCommandList elifCondition = ParseList();
                    ExpectReserved("then");
                    branches.Add(new ShellIfBranch(elifCondition, ParseList()));
                    continue;
                }

                if (Current.IsReserved("else"))
                {
                    _index++;
                    elseBody = ParseList();
                }

                ExpectReserved("fi");
                return new ShellIfCommand(branches, elseBody);
            }
        }

        private ShellLoopCommand ParseLoop()
        {
            bool until = Current.Word!.PlainText == "until";
            _index++;
            ShellCommandList condition = ParseList();
            ShellCommandList body = ParseDoGroup();
            return new ShellLoopCommand(condition, body, until);
        }

        private ShellCommandList ParseDoGroup()
        {
            ExpectReserved("do");
            ShellCommandList body = ParseList();
            ExpectReserved("done");
            return body;
        }

        private ShellForCommand ParseFor()
        {
            _index++; // for
            Token nameToken = Current;
            if (nameToken.Kind != TokenKind.Word || nameToken.Word!.PlainText is not { } name || !IsName(name))
            {
                throw nameToken.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(nameToken);
            }

            _index++;
            List<ShellWord>? items = null;
            SkipNewlines();
            if (Current.IsReserved("in"))
            {
                _index++;
                items = [];
                while (Current.Kind == TokenKind.Word)
                {
                    items.Add(Current.Word!);
                    _index++;
                }

                if (Current.IsOperator(";") || Current.Kind == TokenKind.Newline)
                {
                    _index++;
                }
                else if (Current.Kind == TokenKind.End)
                {
                    throw UnexpectedEnd();
                }
                else
                {
                    throw Unexpected(Current);
                }
            }
            else if (Current.IsOperator(";"))
            {
                _index++;
            }

            SkipNewlines();
            return new ShellForCommand(name, items, ParseDoGroup());
        }

        private ShellCaseCommand ParseCase()
        {
            _index++; // case
            Token subject = Current;
            if (subject.Kind != TokenKind.Word)
            {
                throw subject.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(subject);
            }

            _index++;
            SkipNewlines();
            ExpectReserved("in");
            SkipNewlines();
            List<ShellCaseItem> items = [];
            while (!Current.IsReserved("esac"))
            {
                if (Current.Kind == TokenKind.End)
                {
                    throw UnexpectedEnd();
                }

                if (Current.IsOperator("("))
                {
                    _index++;
                }

                List<ShellWord> patterns = [];
                while (true)
                {
                    if (Current.Kind != TokenKind.Word)
                    {
                        throw Current.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(Current);
                    }

                    patterns.Add(Current.Word!);
                    _index++;
                    if (Current.IsOperator("|"))
                    {
                        _index++;
                        continue;
                    }

                    break;
                }

                Expect(")");
                SkipNewlines();
                ShellCommandList? body = null;
                if (!Current.IsOperator(";;") && !Current.IsReserved("esac"))
                {
                    body = ParseList();
                }

                items.Add(new ShellCaseItem(patterns, body));
                if (Current.IsOperator(";;"))
                {
                    _index++;
                    SkipNewlines();
                }
                else if (!Current.IsReserved("esac"))
                {
                    throw Current.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(Current);
                }
            }

            _index++; // esac
            return new ShellCaseCommand(subject.Word!, items);
        }

        private ShellFunctionDefinition ParseFunctionDefinition()
        {
            Token nameToken = Current;
            string name = nameToken.Word!.PlainText!;
            if (!IsFunctionName(name))
            {
                throw new SyntaxException(ShellSyntaxError.Create(
                    ShellSyntaxErrorCodes.BadFunctionName, nameToken.Offset, nameToken.Length, "Syntax.BadFunctionName", name));
            }

            _index += 3; // name ( )
            SkipNewlines();
            return new ShellFunctionDefinition(name, ParseFunctionBody());
        }

        private ShellFunctionDefinition ParseFunctionKeyword()
        {
            _index++; // function
            Token nameToken = Current;
            if (nameToken.Kind != TokenKind.Word || nameToken.Word!.PlainText is not { } name || !IsFunctionName(name))
            {
                throw nameToken.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(nameToken);
            }

            _index++;
            if (Current.IsOperator("(") && tokens[_index + 1].IsOperator(")"))
            {
                _index += 2;
            }

            SkipNewlines();
            return new ShellFunctionDefinition(name, ParseFunctionBody());
        }

        private ShellCommand ParseFunctionBody()
        {
            Token token = Current;
            bool compound = token.IsOperator("(")
                || (token.Kind == TokenKind.Word && token.Word!.PlainText is "{" or "if" or "while" or "until" or "for" or "case");
            if (!compound)
            {
                throw token.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(token);
            }

            return ParseCommand();
        }

        private static bool IsFunctionName(string name) =>
            name.Length > 0 && !ReservedWords.Contains(name) && name.All(ch => IsNameChar(ch) || ch is '-' or '.' or ':') && !char.IsAsciiDigit(name[0]);

        private void Expect(string op)
        {
            if (!Current.IsOperator(op))
            {
                throw Current.Kind == TokenKind.End ? UnexpectedEnd() : Unexpected(Current);
            }

            _index++;
        }

        private void ExpectReserved(string word)
        {
            if (!Current.IsReserved(word))
            {
                throw Current.Kind == TokenKind.End ? UnexpectedEnd(word) : Unexpected(Current);
            }

            _index++;
        }

        private void SkipNewlines()
        {
            while (Current.Kind == TokenKind.Newline)
            {
                _index++;
            }
        }

        private static SyntaxException Unexpected(Token token) =>
            new(ShellSyntaxError.Create(
                ShellSyntaxErrorCodes.UnexpectedToken,
                token.Offset,
                Math.Max(1, token.Length),
                "Syntax.UnexpectedToken",
                token.Kind == TokenKind.Newline ? "newline" : token.Text));

        private SyntaxException UnexpectedEnd(string? expected = null)
        {
            Token? last = _index > 0 ? tokens[_index - 1] : null;
            ShellSyntaxError error = expected is not null
                ? ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnexpectedEnd, endOffset, 0, "Syntax.UnexpectedEndExpecting", expected)
                : last is { Kind: TokenKind.Operator }
                    ? ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnexpectedEnd, endOffset, 0, "Syntax.UnexpectedEndAfter", last.Text)
                    : ShellSyntaxError.Create(ShellSyntaxErrorCodes.UnexpectedEnd, endOffset, 0, "Syntax.UnexpectedEnd");
            return new SyntaxException(error);
        }
    }

    /// <summary>Unwinds the recursive descent to the first error.</summary>
    private sealed class SyntaxException(ShellSyntaxError error) : Exception(error.Message)
    {
        public ShellSyntaxError Error { get; } = error;
    }
}
