namespace Kazo.Os.AppSdk;

/// <summary>
/// Provides renderer-independent command input, output, and shell state.
/// </summary>
public sealed class TerminalExecutionContext
{
    /// <summary>
    /// Initializes a command execution context.
    /// </summary>
    /// <param name="app">Scoped application execution context.</param>
    /// <param name="arguments">Parsed positional command arguments.</param>
    /// <param name="standardInput">Command standard input stream.</param>
    /// <param name="standardOutput">Command standard output stream.</param>
    /// <param name="standardError">Command standard error stream.</param>
    /// <param name="workingDirectory">Current virtual filesystem directory.</param>
    /// <param name="environment">Read-only shell environment for this execution.</param>
    /// <param name="fullScreen">Optional renderer-independent alternate-screen session.</param>
    /// <param name="shell">
    /// The calling shell, for a command that declares <c>shell.control</c> and was started by an
    /// interactive shell (ADR 0051, section 3); <see langword="null"/> otherwise.
    /// </param>
    public TerminalExecutionContext(
        IAppExecutionContext app,
        IReadOnlyList<string> arguments,
        TextReader standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        IFullScreenTerminalSession? fullScreen = null,
        IShellSession? shell = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);

        App = app;
        Arguments = arguments;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        WorkingDirectory = workingDirectory;
        Environment = environment;
        FullScreen = fullScreen;
        Shell = shell;
    }

    /// <summary>Gets the scoped application execution context.</summary>
    public IAppExecutionContext App { get; }

    /// <summary>Gets parsed positional command arguments.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the renderer-independent standard input stream.</summary>
    public TextReader StandardInput { get; }

    /// <summary>Gets the renderer-independent standard output stream.</summary>
    public TextWriter StandardOutput { get; }

    /// <summary>Gets the renderer-independent standard error stream.</summary>
    public TextWriter StandardError { get; }

    /// <summary>Gets the current virtual filesystem directory.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Gets the read-only environment variables for this execution.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>
    /// Gets the optional full-screen terminal session. Ordinary stream commands can ignore it.
    /// </summary>
    public IFullScreenTerminalSession? FullScreen { get; }

    /// <summary>
    /// Gets the shell that ran this command, so <c>cd</c>, <c>export</c>, <c>help</c> or
    /// <c>sudo</c> can act on it (ADR 0051, section 3). Non-null only when the command declares
    /// <c>shell.control</c> and an interactive shell started it; a command run by an app through
    /// <c>ExecuteCommandIntent</c> gets <see langword="null"/>.
    /// </summary>
    public IShellSession? Shell { get; }

    /// <summary>
    /// Reads one line without echoing it, as <c>getpass(3)</c> does for <c>passwd</c> (ADR 0120): writes
    /// <paramref name="prompt"/> to standard output, turns the terminal's echo off while the line is
    /// typed, then turns it back on. When standard input is not a terminal (a pipe), the line is
    /// read as is.
    /// </summary>
    /// <param name="prompt">Text shown before the hidden input, e.g. <c>New password: </c>.</param>
    /// <param name="cancellationToken">Cancels the read (Ctrl+C).</param>
    /// <returns>The typed line, without its newline, or <see langword="null"/> at end of input.</returns>
    public async ValueTask<string?> ReadSecretLineAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        await StandardOutput.WriteAsync(prompt).ConfigureAwait(false);
        await StandardOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
        ITerminalInputMode? tty = StandardInput as ITerminalInputMode;
        bool echo = tty?.EchoEnabled ?? false;
        if (tty is not null)
        {
            tty.EchoEnabled = false;
        }

        try
        {
            return await StandardInput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (tty is not null)
            {
                tty.EchoEnabled = echo;
            }
        }
    }
}

/// <summary>
/// The mode of a terminal's standard input, the part of <c>termios</c> a command may change
/// (ADR 0120): implemented by the terminal's input, absent on a pipe or a file.
/// </summary>
public interface ITerminalInputMode
{
    /// <summary>
    /// Gets or sets whether typed characters are shown (<c>ECHO</c>). Off while a password is typed;
    /// Enter still moves to the next line.
    /// </summary>
    bool EchoEnabled { get; set; }
}
