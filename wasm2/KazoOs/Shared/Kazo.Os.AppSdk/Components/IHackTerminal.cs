namespace Kazo.Os.AppSdk.Components;

/// <summary>
/// Controls an embedded terminal (contract <see cref="KazoComponentContracts.Terminal"/>, ADR 0063):
/// the host app drives it as a person at the keyboard would, while the terminal keeps its own shell
/// process, prompt, history and screen.
/// </summary>
/// <remarks>
/// <para>
/// The terminal is a child of the host app's process: its shell runs with the host's user, its
/// capabilities (<c>shell.host</c> is required) and its environment, exactly as a terminal the
/// host would draw itself.
/// </para>
/// <para>
/// Every member must be called from the host component's renderer (its event handlers and
/// lifecycle methods); events are raised there too.
/// </para>
/// </remarks>
public interface IHackTerminal
{
    /// <summary>Gets the shell's current directory, or <see langword="null"/> before it started or after it exited.</summary>
    string? WorkingDirectory { get; }

    /// <summary>Gets the title a tab shows: the running command's name, otherwise the directory's name.</summary>
    string Title { get; }

    /// <summary>Gets whether the terminal's shell is running (it has started and has not exited).</summary>
    bool IsRunning { get; }

    /// <summary>Gets whether a command is running in the terminal.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Raised for every piece of text the terminal shows (command output, prompt, echo), without
    /// its colour and cursor control sequences.
    /// </summary>
    event EventHandler<KazoTerminalOutputEventArgs>? OutputReceived;

    /// <summary>Raised when a command line finished, whether it was typed or sent by the host.</summary>
    event EventHandler<KazoTerminalCommandResult>? CommandCompleted;

    /// <summary>Raised when <see cref="Title"/> or <see cref="WorkingDirectory"/> changed.</summary>
    event EventHandler? StateChanged;

    /// <summary>Raised once the shell has exited (<c>exit</c>, Ctrl+D).</summary>
    event EventHandler? Exited;

    /// <summary>
    /// Changes the shell's directory by running <c>cd</c> in it, visibly, as a person would; the line
    /// being typed is kept and comes back after the prompt.
    /// </summary>
    /// <param name="path">Absolute, relative or <c>~</c>-prefixed directory.</param>
    /// <param name="cancellationToken">Cancels the wait for a running command; <c>cd</c> itself is instant.</param>
    /// <returns>The result of <c>cd</c>: exit status 0 when the directory changed.</returns>
    /// <exception cref="ArgumentException">The path is empty or spans several lines.</exception>
    /// <exception cref="InvalidOperationException">The shell is not running.</exception>
    Task<KazoTerminalCommandResult> SetWorkingDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs one command line in the terminal, shown at the prompt as if typed, and waits until it
    /// ends. When another command is running, this one waits for its turn, as typed-ahead input.
    /// </summary>
    /// <param name="commandLine">One line of <c>hsh</c> syntax (pipes, redirections, variables).</param>
    /// <param name="cancellationToken">Interrupts the command, as Ctrl+C.</param>
    /// <returns>The command's exit status and the text it printed.</returns>
    /// <exception cref="ArgumentException">The line is empty or spans several lines.</exception>
    /// <exception cref="InvalidOperationException">The shell is not running.</exception>
    Task<KazoTerminalCommandResult> RunCommandAsync(string commandLine, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends keyboard input, as typed or pasted: printable text goes to the line being edited (or to
    /// the running full-screen program), <c>\r</c> or <c>\n</c> presses Enter, and control
    /// characters act as their keys (<c>\u0003</c> is Ctrl+C).
    /// </summary>
    /// <param name="text">Characters or terminal key sequences.</param>
    Task SendInputAsync(string text);

    /// <summary>Gets the most recent text the terminal showed, without control sequences.</summary>
    /// <param name="maxCharacters">Most characters returned, counted from the end.</param>
    string ReadOutput(int maxCharacters = 65_536);

    /// <summary>Clears the screen and the scrollback, keeping the line being typed (as <c>clear</c>).</summary>
    Task ClearAsync();

    /// <summary>Moves the keyboard focus to the terminal.</summary>
    Task FocusAsync();
}

/// <summary>Options a host passes to an embedded terminal when it creates it.</summary>
public sealed record KazoTerminalOptions
{
    /// <summary>Gets the directory the shell starts in; the host's directory when <see langword="null"/>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets whether the welcome banner is printed before the first prompt.</summary>
    public bool ShowBanner { get; init; } = true;

    /// <summary>Gets whether the terminal takes the keyboard focus once it is shown.</summary>
    public bool FocusOnStart { get; init; } = true;
}

/// <summary>Text shown by an embedded terminal (see <see cref="IHackTerminal.OutputReceived"/>).</summary>
/// <param name="text">The text, without colour and cursor control sequences.</param>
public sealed class KazoTerminalOutputEventArgs(string text) : EventArgs
{
    /// <summary>Gets the text, without colour and cursor control sequences.</summary>
    public string Text { get; } = text;
}

/// <summary>A finished command line of an embedded terminal.</summary>
/// <param name="CommandLine">The line as the shell ran it.</param>
/// <param name="ExitStatus">Its exit status (<c>$?</c>): 0 for success, 130 when interrupted.</param>
/// <param name="Output">What it printed on standard output and error, without control sequences.</param>
public sealed record KazoTerminalCommandResult(string CommandLine, int ExitStatus, string Output);
