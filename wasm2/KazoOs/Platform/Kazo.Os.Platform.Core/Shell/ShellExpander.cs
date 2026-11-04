using System.Globalization;
using System.Text;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// An expansion that stops the command (<c>${x:?msg}</c>, an unbound variable under <c>set -u</c>,
/// bad arithmetic). <see cref="Exception.Message"/> is English; the shell prints the
/// <see cref="IShellLocalizableError.TextKey"/> text in its own language (ADR 0104).
/// </summary>
public sealed class ShellExpansionException : Exception, IShellLocalizableError
{
    /// <summary>Creates the error from a resource text.</summary>
    /// <param name="text">The message as a resource key and its arguments.</param>
    public ShellExpansionException(ShellLocalizedText text)
        : base((text ?? throw new ArgumentNullException(nameof(text))).ToString())
    {
        TextKey = text.TextKey;
        TextArguments = text.TextArguments;
    }

    /// <summary>Wraps an arithmetic error, keeping its text.</summary>
    /// <param name="inner">The arithmetic error.</param>
    public ShellExpansionException(ShellArithmeticException inner)
        : base((inner ?? throw new ArgumentNullException(nameof(inner))).Message, inner)
    {
        TextKey = inner.TextKey;
        TextArguments = inner.TextArguments;
    }

    /// <inheritdoc />
    public string TextKey { get; }

    /// <inheritdoc />
    public IReadOnlyList<object> TextArguments { get; }
}

/// <summary>
/// Word expansion in POSIX order (ADR 0064, superseding the "no word splitting" rule of ADR 0050
/// section 3): tilde, parameter, command and arithmetic expansion, then field splitting on
/// <c>IFS</c> of the unquoted results, then pathname expansion (globbing) of unquoted pattern
/// characters, then quote removal. One expander serves one simple command.
/// </summary>
internal sealed class ShellExpander
{
    private readonly ShellSession _session;
    private readonly Func<ShellCommandList, Task<string>> _substitute;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Creates an expander for <paramref name="session"/>.</summary>
    /// <param name="session">Variables, positional parameters, options and directory.</param>
    /// <param name="substitute">Runs a command substitution in a subshell and returns its output.</param>
    /// <param name="cancellationToken">Cancels pathname expansion and substitutions.</param>
    public ShellExpander(ShellSession session, Func<ShellCommandList, Task<string>> substitute, CancellationToken cancellationToken)
    {
        _session = session;
        _substitute = substitute;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Gets whether a command substitution ran, and the exit status of the last one (the status of <c>x=$(false)</c>).</summary>
    public int? LastSubstitutionStatus { get; private set; }

    /// <summary>Expands words into fields: split, globbed, quotes removed.</summary>
    public async Task<List<string>> ExpandFieldsAsync(IEnumerable<ShellWord> words)
    {
        List<string> fields = [];
        foreach (ShellWord word in words)
        {
            FieldBuilder builder = new(_session.GetVariable("IFS") ?? " \t\n");
            await AppendWordAsync(builder, word, split: true).ConfigureAwait(false);
            foreach (FinishedField field in builder.Finish())
            {
                if (!_session.Options.Contains('f') && field.HasWildcards)
                {
                    IReadOnlyList<string> matches = await ExpandPathnameAsync(field.Pattern).ConfigureAwait(false);
                    if (matches.Count > 0)
                    {
                        fields.AddRange(matches);
                        continue;
                    }
                }

                fields.Add(field.Text);
            }
        }

        return fields;
    }

    /// <summary>Expands a word to one string, without splitting or globbing (assignments, redirection targets, here-documents).</summary>
    public async Task<string> ExpandStringAsync(ShellWord word)
    {
        FieldBuilder builder = new(ifs: string.Empty);
        await AppendWordAsync(builder, word, split: false).ConfigureAwait(false);
        return string.Join(' ', builder.Finish().Select(field => field.Text));
    }

    /// <summary>
    /// Expands a word to a shell pattern (<c>case</c>, <c>${x#p}</c>): characters that came from
    /// quotes or expansions inside quotes are escaped, so they only match themselves.
    /// </summary>
    public async Task<string> ExpandPatternAsync(ShellWord word)
    {
        FieldBuilder builder = new(ifs: string.Empty);
        await AppendWordAsync(builder, word, split: false).ConfigureAwait(false);
        return string.Concat(builder.Finish().Select(field => field.Pattern));
    }

    private async Task AppendWordAsync(FieldBuilder builder, ShellWord word, bool split)
    {
        foreach (ShellWordPart part in word.Parts)
        {
            switch (part)
            {
                case ShellLiteralPart literal:
                    builder.Append(literal.Text, pattern: !literal.Quoted);
                    if (literal.Quoted)
                    {
                        builder.MarkPresent();
                    }

                    break;

                case ShellTildePart:
                    builder.Append(_session.HomeDirectory, pattern: false);
                    builder.MarkPresent();
                    break;

                case ShellSpecialParameterPart { Name: "@" or "*" } all:
                    AppendPositional(builder, all, split);
                    break;

                default:
                    string value = await ExpandValueAsync(part).ConfigureAwait(false);
                    if (part.IsQuoted || !split)
                    {
                        builder.Append(value, pattern: !part.IsQuoted);
                        builder.MarkPresent();
                    }
                    else
                    {
                        builder.AppendSplit(value);
                    }

                    break;
            }
        }
    }

    /// <summary><c>$@</c> and <c>$*</c>: <c>"$@"</c> gives one field per parameter, <c>"$*"</c> joins them with the first IFS character.</summary>
    private void AppendPositional(FieldBuilder builder, ShellSpecialParameterPart part, bool split)
    {
        IReadOnlyList<string> parameters = _session.PositionalParameters;
        if (part.Quoted && part.Name == "@" && split)
        {
            for (int index = 0; index < parameters.Count; index++)
            {
                if (index > 0)
                {
                    builder.EndField(force: true);
                }

                builder.Append(parameters[index], pattern: false);
                builder.MarkPresent();
            }

            return;
        }

        string ifs = _session.GetVariable("IFS") ?? " \t\n";
        string joined = string.Join(part.Quoted || !split ? (ifs.Length > 0 ? ifs[0].ToString() : string.Empty) : " ", parameters);
        if (part.Quoted || !split)
        {
            builder.Append(joined, pattern: false);
            builder.MarkPresent();
            return;
        }

        foreach (string parameter in parameters)
        {
            builder.AppendSplit(parameter);
            builder.EndField(force: false);
        }
    }

    /// <summary>Computes the value of one expansion part.</summary>
    private async Task<string> ExpandValueAsync(ShellWordPart part)
    {
        switch (part)
        {
            case ShellVariablePart variable:
                return GetParameter(variable.Name, checkUnset: true) ?? string.Empty;

            case ShellExitStatusPart:
                return _session.LastExitCode.ToString(CultureInfo.InvariantCulture);

            case ShellSpecialParameterPart special:
                return GetParameter(special.Name, checkUnset: true) ?? string.Empty;

            case ShellParameterExpansionPart expansion:
                return await ExpandParameterOperatorAsync(expansion).ConfigureAwait(false);

            case ShellCommandSubstitutionPart substitution:
                if (substitution.Body is null)
                {
                    LastSubstitutionStatus = 0;
                    return string.Empty;
                }

                string output = await _substitute(substitution.Body).ConfigureAwait(false);
                LastSubstitutionStatus = _session.LastExitCode;
                // $(…) drops every trailing newline, as POSIX requires.
                return output.TrimEnd('\n');

            case ShellArithmeticPart arithmetic:
                string expression = await ExpandStringAsync(arithmetic.Expression).ConfigureAwait(false);
                return EvaluateArithmetic(expression).ToString(CultureInfo.InvariantCulture);

            default:
                return string.Empty;
        }
    }

    /// <summary>Evaluates arithmetic with the shell's variables.</summary>
    public long EvaluateArithmetic(string expression)
    {
        ShellArithmetic arithmetic = new(
            name => GetParameter(name, checkUnset: false),
            (name, value) => _session.AssignVariable(name, value));
        try
        {
            return arithmetic.Evaluate(expression);
        }
        catch (ShellArithmeticException exception)
        {
            throw new ShellExpansionException(exception);
        }
    }

    /// <summary>Reads a variable, positional or special parameter; <see langword="null"/> when unset.</summary>
    private string? GetParameter(string name, bool checkUnset)
    {
        string? value = name switch
        {
            "#" => _session.PositionalParameters.Count.ToString(CultureInfo.InvariantCulture),
            "@" or "*" => string.Join(' ', _session.PositionalParameters),
            "?" => _session.LastExitCode.ToString(CultureInfo.InvariantCulture),
            "$" => (_session.ProcessId?.Value ?? 0).ToString(CultureInfo.InvariantCulture),
            "!" => _session.LastBackgroundProcessId?.ToString(CultureInfo.InvariantCulture),
            "-" => string.Concat(_session.Options.Order()),
            "0" => _session.ScriptName,
            _ when name.All(char.IsAsciiDigit) => GetPositional(int.Parse(name, CultureInfo.InvariantCulture)),
            _ => _session.GetVariable(name)
        };

        if (value is null && checkUnset && _session.Options.Contains('u') && name is not ("@" or "*" or "!"))
        {
            throw new ShellExpansionException(new ShellLocalizedText("Shell.UnboundVariable", name));
        }

        return value;
    }

    private string? GetPositional(int index) =>
        index >= 1 && index <= _session.PositionalParameters.Count ? _session.PositionalParameters[index - 1] : null;

    /// <summary><c>${name op word}</c>.</summary>
    private async Task<string> ExpandParameterOperatorAsync(ShellParameterExpansionPart part)
    {
        string? value = GetParameter(part.Name, checkUnset: part.Operator is "length" or "#" or "##" or "%" or "%%");
        bool colon = part.Operator.StartsWith(':');
        bool missing = value is null || (colon && value.Length == 0);
        switch (part.Operator)
        {
            case "length":
                return (value ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture);
            case ":-" or "-":
                return missing ? await ExpandArgumentAsync(part) : value!;
            case ":=" or "=":
                if (!missing)
                {
                    return value!;
                }

                if (!ShellParser.IsName(part.Name))
                {
                    throw new ShellExpansionException(new ShellLocalizedText("Shell.CannotAssign", part.Name));
                }

                string assigned = await ExpandArgumentAsync(part).ConfigureAwait(false);
                _session.AssignVariable(part.Name, assigned);
                return assigned;
            case ":+" or "+":
                return missing ? string.Empty : await ExpandArgumentAsync(part);
            case ":?" or "?":
                if (!missing)
                {
                    return value!;
                }

                string message = part.Argument is null ? string.Empty : await ExpandArgumentAsync(part);
                throw new ShellExpansionException(message.Length > 0
                    ? new ShellLocalizedText("Shell.Error", part.Name, message)
                    : new ShellLocalizedText(value is null ? "Shell.ParameterNotSet" : "Shell.ParameterNullOrNotSet", part.Name));
            default:
                string pattern = part.Argument is null ? string.Empty : await ExpandPatternAsync(part.Argument).ConfigureAwait(false);
                return ShellPattern.RemoveAffix(
                    value ?? string.Empty,
                    pattern,
                    fromStart: part.Operator.StartsWith('#'),
                    longest: part.Operator.Length == 2);
        }
    }

    private Task<string> ExpandArgumentAsync(ShellParameterExpansionPart part) =>
        part.Argument is null ? Task.FromResult(string.Empty) : ExpandStringAsync(part.Argument);

    /// <summary>
    /// Pathname expansion of one field (POSIX 2.13.3): each component holding a wildcard is
    /// matched against the directory's entries, with the shell's identity and directory; a name
    /// starting with <c>.</c> only matches a pattern that starts with a literal <c>.</c>. Returns
    /// the sorted matches, or nothing (the field then stays as typed).
    /// </summary>
    private async Task<IReadOnlyList<string>> ExpandPathnameAsync(string pattern)
    {
        if (_session.Host is null)
        {
            return [];
        }

        bool absolute = pattern.StartsWith('/');
        string[] components = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            return [];
        }

        List<string> candidates = [absolute ? "/" : string.Empty];
        try
        {
            using (KazoExecutionContext.Enter(_session.Host, _session.CurrentDirectory))
            {
                for (int index = 0; index < components.Length; index++)
                {
                    string component = components[index];
                    bool last = index == components.Length - 1;
                    List<string> next = [];
                    foreach (string prefix in candidates)
                    {
                        _cancellationToken.ThrowIfCancellationRequested();
                        if (!ShellPattern.HasWildcards(component))
                        {
                            string candidate = Join(prefix, ShellPattern.Unescape(component));
                            if (last || await KazoDirectory.ExistsAsync(Resolve(candidate), _cancellationToken).ConfigureAwait(false))
                            {
                                next.Add(candidate);
                            }

                            continue;
                        }

                        string directory = Resolve(prefix.Length == 0 ? "." : prefix);
                        if (!await KazoDirectory.ExistsAsync(directory, _cancellationToken).ConfigureAwait(false))
                        {
                            continue;
                        }

                        bool matchHidden = component.StartsWith('.') || component.StartsWith("\\.", StringComparison.Ordinal);
                        string[] entries = last
                            ? await KazoDirectory.GetFileSystemEntriesAsync(directory, cancellationToken: _cancellationToken).ConfigureAwait(false)
                            : await KazoDirectory.GetDirectoriesAsync(directory, cancellationToken: _cancellationToken).ConfigureAwait(false);
                        foreach (string entry in entries.Order(StringComparer.Ordinal))
                        {
                            string name = KazoPath.GetFileName(entry) ?? string.Empty;
                            if (name.Length == 0 || (name.StartsWith('.') && !matchHidden))
                            {
                                continue;
                            }

                            if (ShellPattern.IsMatch(name, component))
                            {
                                next.Add(Join(prefix, name));
                            }
                        }
                    }

                    candidates = next;
                    if (candidates.Count == 0)
                    {
                        return [];
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable directory matches nothing, as in bash.
            return [];
        }

        if (pattern.EndsWith('/'))
        {
            candidates = [.. candidates.Select(candidate => candidate + "/")];
        }

        return [.. candidates.Order(StringComparer.Ordinal)];
    }

    private static string Join(string prefix, string name) =>
        prefix.Length == 0 ? name : prefix.EndsWith('/') ? prefix + name : prefix + "/" + name;

    private string Resolve(string path) => KazoPath.GetFullPath(path, _session.CurrentDirectory);

    /// <summary>One field being built: its text, and the same text as a pattern (quoted characters escaped).</summary>
    private sealed class Field
    {
        public StringBuilder Text { get; } = new();

        public StringBuilder Pattern { get; } = new();

        public bool Present { get; set; }

        public bool HasWildcardCharacters { get; set; }
    }

    /// <summary>A finished field.</summary>
    private sealed record FinishedField(string Text, string Pattern, bool HasWildcards);

    /// <summary>Accumulates characters into fields, splitting unquoted expansion results on IFS.</summary>
    private sealed class FieldBuilder(string ifs)
    {
        private readonly List<FinishedField> _fields = [];
        private Field _current = new();

        public void Append(string text, bool pattern)
        {
            foreach (char ch in text)
            {
                _current.Text.Append(ch);
                if (pattern)
                {
                    _current.Pattern.Append(ch);
                    if (ch is '*' or '?' or '[')
                    {
                        _current.HasWildcardCharacters = true;
                    }
                }
                else
                {
                    _current.Pattern.Append(ch is '\\' or '*' or '?' or '[' or ']' ? "\\" + ch : ch.ToString());
                }
            }
        }

        public void MarkPresent() => _current.Present = true;

        /// <summary>Appends an unquoted expansion result, cutting it into fields on IFS (POSIX 2.6.5).</summary>
        public void AppendSplit(string value)
        {
            if (ifs.Length == 0)
            {
                Append(value, pattern: true);
                return;
            }

            bool pendingBreak = false;
            foreach (char ch in value)
            {
                if (ifs.Contains(ch))
                {
                    if (ch is ' ' or '\t' or '\n')
                    {
                        pendingBreak = true;
                    }
                    else
                    {
                        // A non-blank IFS character always delimits a field, possibly an empty one.
                        EndField(force: true);
                        pendingBreak = false;
                    }

                    continue;
                }

                if (pendingBreak)
                {
                    EndField(force: false);
                    pendingBreak = false;
                }

                Append(ch.ToString(), pattern: true);
                _current.Present = true;
            }

            if (pendingBreak)
            {
                EndField(force: false);
            }
        }

        public void EndField(bool force)
        {
            if (force || _current.Present || _current.Text.Length > 0)
            {
                _fields.Add(new FinishedField(_current.Text.ToString(), _current.Pattern.ToString(), _current.HasWildcardCharacters));
            }

            _current = new Field();
        }

        public IReadOnlyList<FinishedField> Finish()
        {
            EndField(force: false);
            return _fields;
        }
    }
}
