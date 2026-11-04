using System.Globalization;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// Raised for an arithmetic expression that cannot be evaluated (<c>$((1/0))</c>, a stray token).
/// <see cref="Exception.Message"/> is English; the shell prints <see cref="IShellLocalizableError.TextKey"/>
/// in its own language (ADR 0104).
/// </summary>
public sealed class ShellArithmeticException : Exception, IShellLocalizableError
{
    /// <summary>Creates the error <c>expression: reason</c>.</summary>
    /// <param name="expression">The expression as typed (trimmed).</param>
    /// <param name="reason">Why it failed, as a resource text.</param>
    public ShellArithmeticException(string expression, ShellLocalizedText reason)
        : base($"{expression}: {reason}")
    {
        TextArguments = [expression, reason];
    }

    /// <inheritdoc />
    public string TextKey => "Shell.Error";

    /// <inheritdoc />
    public IReadOnlyList<object> TextArguments { get; }
}

/// <summary>
/// Evaluates shell arithmetic (<c>$((…))</c>, <c>let</c>-style), on 64-bit signed integers as
/// bash does: C operators with C precedence, variables by bare name (unset or empty is 0),
/// assignments (<c>=</c>, <c>+=</c>…), pre/post increment and decrement, <c>?:</c>, <c>**</c>,
/// and decimal, octal (<c>017</c>) and hexadecimal (<c>0x1f</c>) literals.
/// </summary>
public sealed class ShellArithmetic
{
    private readonly Func<string, string?> _getVariable;
    private readonly Action<string, string> _setVariable;
    private string _text = string.Empty;
    private int _position;
    private int _skip;

    /// <summary>Creates an evaluator over the shell's variables.</summary>
    /// <param name="getVariable">Reads a variable (<see langword="null"/> when unset).</param>
    /// <param name="setVariable">Assigns a variable (assignment and increment operators).</param>
    public ShellArithmetic(Func<string, string?> getVariable, Action<string, string> setVariable)
    {
        _getVariable = getVariable ?? throw new ArgumentNullException(nameof(getVariable));
        _setVariable = setVariable ?? throw new ArgumentNullException(nameof(setVariable));
    }

    /// <summary>Assigns a variable, unless inside the skipped side of a short-circuit.</summary>
    private void Assign(string name, string value)
    {
        if (_skip == 0)
        {
            _setVariable(name, value);
        }
    }

    /// <summary>Evaluates <paramref name="expression"/> (already parameter-expanded).</summary>
    /// <exception cref="ShellArithmeticException">The expression is invalid or divides by zero.</exception>
    public long Evaluate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        _text = expression;
        _position = 0;
        SkipBlanks();
        if (_position >= _text.Length)
        {
            return 0;
        }

        long value = ParseAssignment();
        SkipBlanks();
        if (_position < _text.Length)
        {
            throw Error("Arithmetic.SyntaxError", _text[_position..].Trim());
        }

        return value;
    }

    private ShellArithmeticException Error(string textKey, params object[] arguments) =>
        new(_text.Trim(), new ShellLocalizedText(textKey, arguments));

    private void SkipBlanks()
    {
        while (_position < _text.Length && char.IsWhiteSpace(_text[_position]))
        {
            _position++;
        }
    }

    private bool TryTake(string op)
    {
        SkipBlanks();
        if (string.CompareOrdinal(_text, _position, op, 0, op.Length) == 0)
        {
            _position += op.Length;
            return true;
        }

        return false;
    }

    /// <summary>Peeks at an operator without consuming it, refusing a longer operator that starts the same way.</summary>
    private bool Next(string op, params string[] longer)
    {
        SkipBlanks();
        if (string.CompareOrdinal(_text, _position, op, 0, op.Length) != 0)
        {
            return false;
        }

        foreach (string other in longer)
        {
            if (string.CompareOrdinal(_text, _position, other, 0, other.Length) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private long ParseAssignment()
    {
        SkipBlanks();
        int start = _position;
        if (TryReadName(out string? name))
        {
            SkipBlanks();
            foreach (string op in new[] { "<<=", ">>=", "**=", "+=", "-=", "*=", "/=", "%=", "&=", "^=", "|=" })
            {
                if (TryTake(op))
                {
                    long right = ParseAssignment();
                    long result = Apply(op[..^1], ReadVariable(name), right);
                    Assign(name, result.ToString(CultureInfo.InvariantCulture));
                    return result;
                }
            }

            if (Next("=", "=="))
            {
                _position++;
                long value = ParseAssignment();
                Assign(name, value.ToString(CultureInfo.InvariantCulture));
                return value;
            }

            _position = start;
        }

        return ParseConditional();
    }

    private long ParseConditional()
    {
        long condition = ParseBinary(0);
        if (TryTake("?"))
        {
            // Only the selected branch has side effects.
            long whenTrue = ParseSkippedUnless(condition != 0);
            if (!TryTake(":"))
            {
                throw Error("Arithmetic.ColonExpected");
            }

            long whenFalse = ParseSkippedUnless(condition == 0);
            return condition != 0 ? whenTrue : whenFalse;
        }

        return condition;
    }

    private long ParseSkippedUnless(bool selected)
    {
        if (!selected)
        {
            _skip++;
        }

        try
        {
            return ParseAssignment();
        }
        finally
        {
            if (!selected)
            {
                _skip--;
            }
        }
    }

    private static readonly string[][] Levels =
    [
        ["||"],
        ["&&"],
        ["|"],
        ["^"],
        ["&"],
        ["==", "!="],
        ["<=", ">=", "<", ">"],
        ["<<", ">>"],
        ["+", "-"],
        ["*", "/", "%"]
    ];

    private long ParseBinary(int level)
    {
        if (level == Levels.Length)
        {
            return ParsePower();
        }

        long left = ParseBinary(level + 1);
        while (true)
        {
            string? matched = null;
            foreach (string op in Levels[level])
            {
                string[] longer = op switch
                {
                    "|" => ["||", "|="],
                    "&" => ["&&", "&="],
                    "<" => ["<<", "<="],
                    ">" => [">>", ">="],
                    "*" => ["**", "*="],
                    "+" => ["++", "+="],
                    "-" => ["--", "-="],
                    "/" => ["/="],
                    "%" => ["%="],
                    "^" => ["^="],
                    "<<" => ["<<="],
                    ">>" => [">>="],
                    _ => []
                };
                if (Next(op, longer))
                {
                    matched = op;
                    break;
                }
            }

            if (matched is null)
            {
                return left;
            }

            _position += matched.Length;
            if ((matched is "&&" && left == 0) || (matched is "||" && left != 0))
            {
                // Short-circuit: the right side is parsed, but with no side effect and no error,
                // as in bash (`(( 0 && x++ ))` leaves x alone).
                _skip++;
                try
                {
                    _ = ParseBinary(level + 1);
                }
                finally
                {
                    _skip--;
                }

                left = matched == "&&" ? 0 : 1;
                continue;
            }

            long right = ParseBinary(level + 1);
            left = Apply(matched, left, right);
        }
    }

    private long ParsePower()
    {
        long value = ParseUnary();
        if (Next("**", "**="))
        {
            _position += 2;
            long exponent = ParsePower();
            if (exponent < 0)
            {
                throw Error("Arithmetic.NegativeExponent");
            }

            long result = 1;
            for (long i = 0; i < exponent; i++)
            {
                result = unchecked(result * value);
            }

            return result;
        }

        return value;
    }

    private long ParseUnary()
    {
        SkipBlanks();
        if (TryTake("++") || TryTake("--"))
        {
            bool increment = _text[_position - 1] == '+';
            SkipBlanks();
            if (!TryReadName(out string? name))
            {
                throw Error("Arithmetic.IncrementOperandExpected");
            }

            long value = ReadVariable(name) + (increment ? 1 : -1);
            Assign(name, value.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        if (TryTake("!"))
        {
            return ParseUnary() == 0 ? 1 : 0;
        }

        if (TryTake("~"))
        {
            return ~ParseUnary();
        }

        if (TryTake("-"))
        {
            return unchecked(-ParseUnary());
        }

        if (TryTake("+"))
        {
            return ParseUnary();
        }

        return ParsePostfix();
    }

    private long ParsePostfix()
    {
        SkipBlanks();
        if (TryTake("("))
        {
            long value = ParseAssignment();
            if (!TryTake(")"))
            {
                throw Error("Arithmetic.MissingParenthesis");
            }

            return value;
        }

        if (TryReadName(out string? name))
        {
            long value = ReadVariable(name);
            if (TryTake("++") || TryTake("--"))
            {
                bool increment = _text[_position - 1] == '+';
                Assign(name, (value + (increment ? 1 : -1)).ToString(CultureInfo.InvariantCulture));
            }

            return value;
        }

        return ReadNumber();
    }

    private bool TryReadName(out string name)
    {
        SkipBlanks();
        int start = _position;
        if (_position < _text.Length && (char.IsAsciiLetter(_text[_position]) || _text[_position] == '_'))
        {
            _position++;
            while (_position < _text.Length && (char.IsAsciiLetterOrDigit(_text[_position]) || _text[_position] == '_'))
            {
                _position++;
            }

            name = _text[start.._position];
            return true;
        }

        name = string.Empty;
        return false;
    }

    private long ReadNumber()
    {
        SkipBlanks();
        int start = _position;
        while (_position < _text.Length && (char.IsAsciiLetterOrDigit(_text[_position]) || _text[_position] == '#'))
        {
            _position++;
        }

        string literal = _text[start.._position];
        if (literal.Length == 0)
        {
            throw _position < _text.Length
                ? Error("Arithmetic.OperandExpectedAt", _text[_position..].Trim())
                : Error("Arithmetic.OperandExpected");
        }

        return ParseInteger(literal) ?? throw Error("Arithmetic.ValueTooGreat", literal);
    }

    /// <summary>Parses a shell integer literal: decimal, <c>0</c>-prefixed octal, <c>0x</c> hexadecimal, or <c>base#digits</c>.</summary>
    internal static long? ParseInteger(string literal)
    {
        literal = literal.Trim();
        if (literal.Length == 0)
        {
            return 0;
        }

        bool negative = literal[0] == '-';
        if (literal[0] is '-' or '+')
        {
            literal = literal[1..];
        }

        long? value;
        try
        {
            int hash = literal.IndexOf('#');
            if (hash > 0 && int.TryParse(literal[..hash], NumberStyles.None, CultureInfo.InvariantCulture, out int radix) && radix is >= 2 and <= 36)
            {
                value = ParseRadix(literal[(hash + 1)..], radix);
            }
            else if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = long.TryParse(literal[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long hex) ? hex : null;
            }
            else if (literal.Length > 1 && literal[0] == '0')
            {
                value = ParseRadix(literal[1..], 8);
            }
            else
            {
                value = long.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out long number) ? number : null;
            }
        }
        catch (OverflowException)
        {
            value = null;
        }

        return negative && value is { } positive ? -positive : value;
    }

    private static long? ParseRadix(string digits, int radix)
    {
        if (digits.Length == 0)
        {
            return null;
        }

        long value = 0;
        foreach (char ch in digits)
        {
            int digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'a' and <= 'z' => ch - 'a' + 10,
                >= 'A' and <= 'Z' => ch - 'A' + 10,
                _ => 99
            };
            if (digit >= radix)
            {
                return null;
            }

            value = checked((value * radix) + digit);
        }

        return value;
    }

    private long ReadVariable(string name)
    {
        string? text = _getVariable(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (ParseInteger(text) is { } number)
        {
            return number;
        }

        // bash evaluates a variable holding an expression (x="1+2"); recursion is bounded by nesting.
        ShellArithmetic nested = new(_getVariable, _setVariable);
        return nested.Evaluate(text);
    }

    private long Apply(string op, long left, long right) => op switch
    {
        "||" => left != 0 || right != 0 ? 1 : 0,
        "&&" => left != 0 && right != 0 ? 1 : 0,
        "|" => left | right,
        "^" => left ^ right,
        "&" => left & right,
        "==" => left == right ? 1 : 0,
        "!=" => left != right ? 1 : 0,
        "<=" => left <= right ? 1 : 0,
        ">=" => left >= right ? 1 : 0,
        "<" => left < right ? 1 : 0,
        ">" => left > right ? 1 : 0,
        "<<" => left << (int)(right & 63),
        ">>" => left >> (int)(right & 63),
        "+" => unchecked(left + right),
        "-" => unchecked(left - right),
        "*" => unchecked(left * right),
        "**" => Power(left, right),
        "/" or "%" when right == 0 && _skip > 0 => 0,
        "/" => right == 0 ? throw Error("Arithmetic.DivisionByZero") : left / right,
        "%" => right == 0 ? throw Error("Arithmetic.DivisionByZero") : left % right,
        _ => throw Error("Arithmetic.UnknownOperator", op)
    };

    private long Power(long value, long exponent)
    {
        if (exponent < 0)
        {
            throw Error("Arithmetic.NegativeExponent");
        }

        long result = 1;
        for (long i = 0; i < exponent; i++)
        {
            result = unchecked(result * value);
        }

        return result;
    }
}
