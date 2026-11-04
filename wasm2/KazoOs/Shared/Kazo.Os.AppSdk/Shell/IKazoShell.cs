namespace Kazo.Os.AppSdk.Shell;

/// <summary>
/// An interactive <c>hsh</c> shell hosted by a terminal emulator (ADR 0056, section 7): what a
/// terminal sees of the shell it runs, as <c>gnome-terminal</c> sees the <c>bash</c> it forked.
/// </summary>
/// <remarks>
/// <para>
/// The shell is its own <b>process</b>, a child of the app that started it, with its own working
/// directory, environment, history and <c>$?</c> (ADR 0050). The commands it runs are its
/// children: terminal → shell → command, as under Linux. Each command runs with <b>its own</b>
/// capabilities, never the host app's.
/// </para>
/// <para>
/// Not to be confused with <see cref="IShellSession"/> (ADR 0051): that is what a <b>command</b>
/// sees of the shell that launched it (<c>shell.control</c>); this is what a <b>terminal</b> sees
/// of the shell it hosts (<c>shell.host</c>).
/// </para>
/// <para>
/// One line runs at a time, like a shell reading its terminal. Disposing the shell ends its
/// process and, with it, every command still running (the terminal tab is closed).
/// </para>
/// </remarks>
public interface IKazoShell : IAsyncDisposable
{
    /// <summary>Gets the PID of the shell's process (<c>$$</c>).</summary>
    int ProcessId { get; }

    /// <summary>Gets the shell's current working directory (changed by <c>cd</c>).</summary>
    string WorkingDirectory { get; }

    /// <summary>Gets the exit status of the last pipeline (<c>$?</c>).</summary>
    int ExitStatus { get; }

    /// <summary>Gets the prompt to show before the next line: <c>user@host:dir$ </c> without the trailing space.</summary>
    string Prompt { get; }

    /// <summary>Gets whether the shell's process ended (disposed, or killed with its host).</summary>
    bool HasExited { get; }

    /// <summary>Gets the lines entered so far, oldest first (what <c>history</c> lists).</summary>
    IReadOnlyList<string> History { get; }

    /// <summary>Records a line the user entered and resets history navigation.</summary>
    /// <param name="commandLine">The line as typed.</param>
    void AddHistory(string commandLine);

    /// <summary>Moves through the history like the arrow keys of <c>readline</c>.</summary>
    /// <param name="delta"><c>-1</c> for the previous line (↑), <c>+1</c> for the next one (↓).</param>
    /// <returns>The line to show, an empty string past the newest line, or <see langword="null"/> when there is nothing to show.</returns>
    string? NavigateHistory(int delta);

    /// <summary>Gets the installed commands, ordered by name (what <c>help</c> lists).</summary>
    IReadOnlyList<ShellCommandInfo> GetInstalledCommands();

    /// <summary>
    /// Gets whether <paramref name="text"/> is an unfinished command (ADR 0064): an <c>if</c>
    /// without <c>fi</c>, a <c>for</c> without <c>done</c>, an open quote, a trailing <c>|</c> or
    /// <c>&amp;&amp;</c>, a here-document waiting for its delimiter. A terminal then asks for a
    /// continuation line with bash's <c>PS2</c> prompt (<c>&gt;</c>).
    /// </summary>
    /// <param name="text">The lines typed so far, joined by newlines.</param>
    bool NeedsMoreInput(string text) => false;

    /// <summary>
    /// Runs one line: parsing, expansion, pipes, redirections, <c>&amp;&amp;</c>, then each command
    /// as a child process of the shell. Output is written as commands produce it.
    /// </summary>
    /// <param name="commandLine">The line as typed.</param>
    /// <param name="standardInput">What the first command of each pipeline reads; <see langword="null"/> for an empty input.</param>
    /// <param name="standardOutput">Where the last command of each pipeline writes. Never closed by the shell.</param>
    /// <param name="standardError">Where every command and the shell write errors. Never closed by the shell.</param>
    /// <param name="cancellationToken">Ctrl+C: stops every command of the running line (exit status 130).</param>
    /// <returns>The exit status of the last pipeline that ran (<c>$?</c>).</returns>
    /// <exception cref="ObjectDisposedException">The shell has exited.</exception>
    Task<int> ExecuteAsync(
        string commandLine,
        TextReader? standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(commandLine, new KazoShellExecution
        {
            StandardInput = standardInput,
            StandardOutput = standardOutput,
            StandardError = standardError
        }, cancellationToken);

    /// <summary>Runs one line with every stream the terminal offers, the alternate screen included.</summary>
    /// <param name="commandLine">The line as typed.</param>
    /// <param name="execution">Streams and alternate screen.</param>
    /// <param name="cancellationToken">Ctrl+C: stops every command of the running line (exit status 130).</param>
    /// <returns>The exit status of the last pipeline that ran (<c>$?</c>).</returns>
    /// <exception cref="ObjectDisposedException">The shell has exited.</exception>
    Task<int> ExecuteAsync(string commandLine, KazoShellExecution execution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the word under the cursor, as <c>bash</c> does on Tab (ADR 0058, section 4): the
    /// first word of a command (start of the line, after <c>|</c>, <c>&amp;&amp;</c> or <c>;</c>)
    /// is completed with the installed commands and their aliases; every other word (and a command
    /// word containing <c>/</c>) with the paths of the shell's directory, <c>~</c> expanded. A
    /// directory ends with <c>/</c>, hidden entries come only after a typed <c>.</c>, special
    /// characters are escaped with <c>\</c>, and a directory the shell cannot read proposes
    /// nothing, without an error.
    /// </summary>
    /// <param name="commandLine">The line being typed.</param>
    /// <param name="cursor">Cursor position in <paramref name="commandLine"/> (0 to its length).</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The candidates, never <see langword="null"/>; empty when nothing matches.</returns>
    Task<KazoShellCompletion> CompleteAsync(string commandLine, int cursor, CancellationToken cancellationToken = default);
}

/// <summary>Options of <see cref="KazoShell.StartAsync(KazoShellOptions, CancellationToken)"/>.</summary>
public sealed record KazoShellOptions
{
    /// <summary>
    /// Gets the starting directory: absolute, relative to the caller's directory, or
    /// <c>~</c>-prefixed. When it is <see langword="null"/>, missing or unreadable, the shell keeps
    /// the caller's directory, as a terminal falls back to the home directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets where the shell asks for credentials (<c>sudo</c>, approvals). Without it, <c>sudo</c>
    /// fails and no approval is offered.
    /// </summary>
    public KazoCredentialPrompt? CredentialPrompt { get; init; }
}

/// <summary>Streams of one line run by <see cref="IKazoShell.ExecuteAsync(string, KazoShellExecution, CancellationToken)"/>.</summary>
public sealed record KazoShellExecution
{
    /// <summary>Gets what the first command of each pipeline reads; <see langword="null"/> for an empty input.</summary>
    public TextReader? StandardInput { get; init; }

    /// <summary>Gets where the last command of each pipeline writes. Never closed by the shell.</summary>
    public required TextWriter StandardOutput { get; init; }

    /// <summary>Gets where every command and the shell write errors. Never closed by the shell.</summary>
    public required TextWriter StandardError { get; init; }

    /// <summary>Gets the alternate screen offered to full-screen commands such as <c>nano</c>.</summary>
    public IFullScreenTerminalSession? FullScreen { get; init; }
}

/// <summary>
/// Result of <see cref="IKazoShell.CompleteAsync"/>: the word being completed and its candidates.
/// </summary>
/// <param name="WordStart">Index in the line where the completed word starts.</param>
/// <param name="WordLength">Length of the word up to the cursor.</param>
/// <param name="Candidates">
/// Matching words, sorted, without duplicates. A directory ends with <c>/</c>, as <c>bash</c>
/// shows it.
/// </param>
public sealed record KazoShellCompletion(int WordStart, int WordLength, IReadOnlyList<string> Candidates)
{
    /// <summary>Gets an empty completion at <paramref name="cursor"/>.</summary>
    /// <param name="cursor">Cursor position.</param>
    public static KazoShellCompletion None(int cursor) => new(cursor, 0, []);

    /// <summary>
    /// Gets the longest prefix shared by every candidate: what Tab inserts when several match.
    /// Empty when there is no candidate.
    /// </summary>
    public string CommonPrefix
    {
        get
        {
            if (Candidates.Count == 0)
            {
                return string.Empty;
            }

            string prefix = Candidates[0];
            foreach (string candidate in Candidates.Skip(1))
            {
                int length = 0;
                while (length < prefix.Length && length < candidate.Length && prefix[length] == candidate[length])
                {
                    length++;
                }

                prefix = prefix[..length];
            }

            return prefix;
        }
    }

    /// <summary>
    /// Gets what a terminal lists on the second Tab, one per candidate: the last path segment,
    /// without its escapes (a directory keeps its <c>/</c>), as bash lists <c>notes/</c> rather
    /// than <c>~/Documents/notes/</c>. Command names are listed as they are.
    /// </summary>
    public IReadOnlyList<string> DisplayCandidates => [.. Candidates.Select(GetDisplayName)];

    /// <summary>
    /// Applies the completion to <paramref name="commandLine"/> as Tab does: a single candidate
    /// replaces the word and is followed by a space (none after a directory's <c>/</c>); several
    /// insert their common prefix; none leave the line alone.
    /// </summary>
    /// <param name="commandLine">The line the completion was computed for.</param>
    /// <param name="cursor">Receives the new cursor position.</param>
    /// <returns>The completed line.</returns>
    public string ApplyTo(string commandLine, out int cursor)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        int wordEnd = WordStart + WordLength;
        string insertion = Candidates.Count switch
        {
            0 => commandLine.Substring(WordStart, WordLength),
            1 => Candidates[0].EndsWith('/') ? Candidates[0] : Candidates[0] + " ",
            _ => CommonPrefix.Length >= WordLength ? CommonPrefix : commandLine.Substring(WordStart, WordLength)
        };

        cursor = WordStart + insertion.Length;
        return string.Concat(commandLine.AsSpan(0, WordStart), insertion, commandLine.AsSpan(wordEnd));
    }

    /// <summary>Last segment of a candidate, unescaped, with a directory's trailing <c>/</c>.</summary>
    private static string GetDisplayName(string candidate)
    {
        bool directory = candidate.Length > 1 && candidate.EndsWith('/');
        string trimmed = directory ? candidate[..^1] : candidate;
        int start = 0;
        for (int index = trimmed.Length - 1; index >= 0; index--)
        {
            if (trimmed[index] == '/' && !IsEscaped(trimmed, index))
            {
                start = index + 1;
                break;
            }
        }

        System.Text.StringBuilder name = new(trimmed.Length - start + 1);
        for (int index = start; index < trimmed.Length; index++)
        {
            if (trimmed[index] == '\\' && index + 1 < trimmed.Length)
            {
                index++;
            }

            name.Append(trimmed[index]);
        }

        return directory ? name.Append('/').ToString() : name.ToString();
    }

    /// <summary>Whether an odd number of backslashes precedes <paramref name="index"/>.</summary>
    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        for (int position = index - 1; position >= 0 && text[position] == '\\'; position--)
        {
            backslashes++;
        }

        return backslashes % 2 == 1;
    }
}
