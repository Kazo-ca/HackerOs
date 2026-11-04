namespace Kazo.Os.AppSdk;

/// <summary>
/// Controlled access to the shell that ran a command (ADR 0051, section 3). As on Linux, <c>cd</c>
/// and <c>export</c> must act on the shell itself rather than on their own process: this is how a
/// catalog command reaches it. Exposed by <see cref="TerminalExecutionContext.Shell"/> only to a
/// command that declares <c>shell.control</c> and was started by an interactive shell.
/// </summary>
/// <remarks>
/// A command alone in its pipeline (including after <c>&amp;&amp;</c>) receives the real shell:
/// <c>cd /tmp &amp;&amp; ls</c> lists <c>/tmp</c>. Inside a pipe (<c>cd /tmp | cat</c>) it receives
/// a copy, like the subshell bash runs its built-ins in there: the command succeeds but its changes
/// are lost when the stage ends.
/// </remarks>
public interface IShellSession
{
    /// <summary>Gets the shell's current working directory (canonical absolute path).</summary>
    string WorkingDirectory { get; }

    /// <summary>Gets a snapshot of the shell's environment.</summary>
    IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>Gets the command lines entered in the shell, oldest first.</summary>
    IReadOnlyList<string> History { get; }

    /// <summary>
    /// Changes the shell's working directory, like the <c>cd</c> built-in: the target is checked
    /// (existence, type, permission to traverse) under the calling command's identity, then the
    /// shell's process directory, <c>PWD</c> and <c>OLDPWD</c> are updated at once, so the rest of
    /// an <c>&amp;&amp;</c> list sees the change.
    /// </summary>
    /// <param name="path">Absolute path, or a path relative to <see cref="WorkingDirectory"/>.</param>
    /// <param name="cancellationToken">Cancels the existence check.</param>
    /// <exception cref="ArgumentException">The path is empty or malformed.</exception>
    /// <exception cref="IO.KazoDirectoryNotFoundException">The path does not exist.</exception>
    /// <exception cref="IO.KazoIOException">The path is not a directory.</exception>
    /// <exception cref="IO.KazoUnauthorizedAccessException">The path cannot be looked up.</exception>
    ValueTask ChangeDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Sets one variable of the shell; commands started afterwards inherit it.</summary>
    /// <exception cref="ArgumentException">The name is not a valid shell identifier, or the value contains NUL.</exception>
    void SetVariable(string name, string value);

    /// <summary>Removes one variable of the shell; nothing happens when it is not set.</summary>
    /// <exception cref="ArgumentException">The name is not a valid shell identifier.</exception>
    void UnsetVariable(string name);

    /// <summary>
    /// Gets the installed commands (name, aliases, usage, description), ordered by name, with
    /// descriptions and usages in the shell's language (its <c>LANG</c>, ADR 0104).
    /// </summary>
    IReadOnlyList<ShellCommandInfo> GetCommands();

    /// <summary>
    /// Gets the installed commands with descriptions and usages in <paramref name="culture"/>,
    /// e.g. the language of the <c>help</c> process asking (ADR 0104, section 4). Defaults to
    /// <see cref="GetCommands()"/> so existing implementations keep compiling.
    /// </summary>
    /// <param name="culture">The language wanted: <c>en</c> or <c>fr</c>.</param>
    IReadOnlyList<ShellCommandInfo> GetCommands(System.Globalization.CultureInfo culture) => GetCommands();

    /// <summary>
    /// Asks the shell to exit once the current command returns, like the <c>exit</c> built-in
    /// (ADR 0058, section 4): the rest of the line is not run and the shell's process ends with
    /// the given status, so its terminal closes the tab. Inside a pipe (<c>exit | cat</c>) the
    /// request reaches a copy of the shell, as in bash's subshell, and is lost.
    /// </summary>
    /// <param name="exitStatus">The shell's exit status; <see langword="null"/> keeps <c>$?</c>.</param>
    /// <returns>The status the shell will exit with (0 to 255).</returns>
    /// <exception cref="NotSupportedException">The shell does not support being exited by a command.</exception>
    int Exit(int? exitStatus) => throw new NotSupportedException("This shell cannot be exited by a command.");

    /// <summary>
    /// Gets one variable of the shell, exported or not (ADR 0064: <c>NAME=value</c> makes an
    /// unexported shell variable that <see cref="Environment"/> does not list), or
    /// <see langword="null"/> when it is not set. <c>export NAME</c> reads it here.
    /// </summary>
    /// <param name="name">Variable name.</param>
    string? GetVariable(string name) => Environment.GetValueOrDefault(name);

    /// <summary>
    /// Assigns a variable as <c>NAME=value</c> does (ADR 0064): it stays exported if it was,
    /// otherwise it becomes an unexported shell variable. Used by <c>read</c>.
    /// </summary>
    /// <param name="name">Variable name, matching <c>[A-Za-z_][A-Za-z0-9_]*</c>.</param>
    /// <param name="value">New value.</param>
    /// <exception cref="ArgumentException">The name is not a valid identifier.</exception>
    void AssignVariable(string name, string value) => SetVariable(name, value);

    /// <summary>
    /// Runs a command line in a copy of the shell (a subshell, ADR 0064), as <c>watch</c> and
    /// <c>sh -c</c> do: full shell syntax, the shell's variables, functions and directory, but
    /// nothing it changes reaches the shell.
    /// </summary>
    /// <param name="commandLine">The command line.</param>
    /// <param name="standardInput">What the commands read.</param>
    /// <param name="standardOutput">Where they write.</param>
    /// <param name="standardError">Where they and the shell write errors.</param>
    /// <param name="cancellationToken">Interrupts the commands.</param>
    /// <returns>The exit status of the line (<c>2</c> for a syntax error).</returns>
    ValueTask<int> RunAsync(
        string commandLine,
        TextReader standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This shell cannot run command lines for a command.");

    /// <summary>Gets the shell's background jobs (<c>command &amp;</c>), oldest first, for <c>jobs</c> and <c>kill %n</c>.</summary>
    IReadOnlyList<ShellJobInfo> Jobs => [];

    /// <summary>
    /// Stops a background job (<c>kill %n</c>): its running commands are killed and the job ends
    /// as terminated.
    /// </summary>
    /// <param name="jobId">The job number shown by <c>jobs</c>.</param>
    /// <param name="force"><see langword="true"/> for <c>SIGKILL</c>, otherwise a graceful stop (<c>SIGTERM</c>).</param>
    /// <returns><see langword="false"/> when no such job exists.</returns>
    bool KillJob(int jobId, bool force) => false;

    /// <summary>
    /// Runs one command as root (ADR 0042, ADR 0051 section 4), as <c>sudo</c> does: the shell asks
    /// for an administrator's credentials, mints a single-use <c>SudoRoot</c> ticket and starts the
    /// command as a child of the caller with it, its output going to <paramref name="standardOutput"/>
    /// and <paramref name="standardError"/>. The target never receives access to the shell.
    /// </summary>
    /// <param name="arguments">The command's <c>argv</c>, command name first.</param>
    /// <param name="standardOutput">Where the command writes its output.</param>
    /// <param name="standardError">Where the command and the shell write errors.</param>
    /// <param name="cancellationToken">Interrupts the prompt or the command.</param>
    /// <returns>The command's exit code, or <c>1</c> (shell built-in, refused credentials) or <c>127</c> (not found).</returns>
    ValueTask<int> RunElevatedAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs one command at another niceness (ADR 0127), as <c>nice</c> does: the command starts as a
    /// child of the caller (the <c>nice</c> process) in the shell's directory, with the caller's
    /// niceness plus <paramref name="adjustment"/>, its output going to <paramref name="standardOutput"/>
    /// and <paramref name="standardError"/>. Like GNU <c>nice</c>, a negative adjustment without root
    /// prints <c>nice: cannot set niceness: Permission denied</c> and runs the command at the caller's
    /// niceness. The target never receives access to the shell. Defaults to refusing, so existing
    /// implementations keep compiling.
    /// </summary>
    /// <param name="arguments">The command's <c>argv</c>, command name first.</param>
    /// <param name="adjustment">Added to the caller's niceness; the result is clamped to <c>-20</c>..<c>19</c>.</param>
    /// <param name="standardOutput">Where the command writes its output.</param>
    /// <param name="standardError">Where the command and the shell write errors.</param>
    /// <param name="cancellationToken">Interrupts the command.</param>
    /// <returns>The command's exit code, or <c>125</c> (shell built-in) or <c>127</c> (not found), GNU <c>nice</c>'s codes.</returns>
    ValueTask<int> RunNicedAsync(
        IReadOnlyList<string> arguments,
        int adjustment,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This shell cannot run a command at another niceness.");
}

/// <summary>One installed command, as listed by <see cref="IShellSession.GetCommands()"/> for <c>help</c>.</summary>
/// <param name="Name">The command name typed in the shell.</param>
/// <param name="Aliases">Other names declared by the command's manifest.</param>
/// <param name="Usage">Usage line from the manifest.</param>
/// <param name="Description">Short description from the manifest.</param>
public sealed record ShellCommandInfo(
    string Name,
    IReadOnlyList<string> Aliases,
    string Usage,
    string Description);

/// <summary>State of a background job.</summary>
public enum ShellJobState
{
    /// <summary>Still running.</summary>
    Running,

    /// <summary>Finished on its own (<see cref="ShellJobInfo.ExitStatus"/> holds its status).</summary>
    Done,

    /// <summary>Stopped by <c>kill</c> or by the shell.</summary>
    Terminated
}

/// <summary>One background job, as listed by <c>jobs</c> (ADR 0064).</summary>
/// <param name="Id">Job number (<c>%1</c>).</param>
/// <param name="Command">The command line as typed, without the trailing <c>&amp;</c>.</param>
/// <param name="State">Running, done or terminated.</param>
/// <param name="ExitStatus">Exit status once done.</param>
/// <param name="ProcessIds">Processes the job started, oldest first (<c>jobs -l</c>, <c>$!</c> is the last one).</param>
/// <param name="IsCurrent"><see langword="true"/> for the current job (<c>+</c> in <c>jobs</c>, <c>%%</c>).</param>
public sealed record ShellJobInfo(
    int Id,
    string Command,
    ShellJobState State,
    int? ExitStatus,
    IReadOnlyList<int> ProcessIds,
    bool IsCurrent);
