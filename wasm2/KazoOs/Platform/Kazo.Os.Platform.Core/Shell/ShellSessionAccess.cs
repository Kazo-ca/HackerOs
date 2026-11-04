using Kazo.Os.AppSdk;

namespace Kazo.Os.Platform.Core.Shell;

/// <summary>
/// The <see cref="IShellSession"/> a command declaring <c>shell.control</c> receives (ADR 0051,
/// section 3): a narrow view over the running <see cref="ShellSession"/> (or over its copy inside a
/// pipe), built by <see cref="ShellInterpreter"/> for one stage. It is what lets the catalog's
/// <c>cd</c>, <c>export</c>, <c>unset</c>, <c>help</c> and <c>sudo</c> replace the names the
/// terminal used to intercept. Under <c>sudo nice</c> (ADR 0136) it also carries sudo's root
/// ticket, which <see cref="RunNicedAsync"/> hands to the command nice runs.
/// </summary>
internal sealed class ShellSessionAccess(
    ShellInterpreter interpreter,
    ShellSession session,
    ShellExecutionOptions options,
    TextReader standardInput,
    ShellElevation? niceElevation = null) : IShellSession
{
    /// <inheritdoc />
    public string WorkingDirectory => session.CurrentDirectory;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Environment => session.GetVariables();

    /// <inheritdoc />
    public IReadOnlyList<string> History => [.. session.History];

    /// <inheritdoc />
    public async ValueTask ChangeDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await session.ChangeDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void SetVariable(string name, string value)
    {
        RequireIdentifier(name);
        ArgumentNullException.ThrowIfNull(value);
        session.SetVariable(name, value);
    }

    /// <inheritdoc />
    public void UnsetVariable(string name)
    {
        RequireIdentifier(name);
        session.UnsetVariable(name);
    }

    /// <inheritdoc />
    public IReadOnlyList<ShellCommandInfo> GetCommands() =>
        interpreter.GetInstalledCommands(Kazo.Os.AppSdk.Globalization.HackLocale.ResolveLanguage(session.GetVariable));

    public IReadOnlyList<ShellCommandInfo> GetCommands(System.Globalization.CultureInfo culture) =>
        interpreter.GetInstalledCommands(culture);

    /// <inheritdoc />
    public int Exit(int? exitStatus) => session.RequestExit(exitStatus ?? session.LastExitCode);

    /// <inheritdoc />
    public async ValueTask<int> RunAsync(
        string commandLine,
        TextReader standardInput,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        return await interpreter.RunInSubshellAsync(
            session, commandLine, options, standardInput, standardOutput, standardError, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public string? GetVariable(string name) => session.GetVariable(name);

    /// <inheritdoc />
    public void AssignVariable(string name, string value)
    {
        RequireIdentifier(name);
        ArgumentNullException.ThrowIfNull(value);
        session.AssignVariable(name, value);
    }

    /// <inheritdoc />
    /// <remarks>Listing ended jobs reports them, as bash's <c>jobs</c> does: they are forgotten afterwards.</remarks>
    public IReadOnlyList<ShellJobInfo> Jobs
    {
        get
        {
            IReadOnlyList<ShellJobInfo> jobs = session.Jobs.Describe();
            foreach (ShellJob job in session.Jobs.Jobs.Where(job => job.State != ShellJobState.Running))
            {
                job.Reported = true;
            }

            session.Jobs.RemoveReported();
            return jobs;
        }
    }

    /// <inheritdoc />
    public bool KillJob(int jobId, bool force) => interpreter.KillJob(session, jobId, force);

    /// <inheritdoc />
    public ValueTask<int> RunElevatedAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken) =>
        // The elevated command reads what sudo would have read: `echo x | sudo tee` keeps its input.
        interpreter.RunElevatedAsync(session, arguments, standardInput, standardOutput, standardError, options, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> RunNicedAsync(
        IReadOnlyList<string> arguments,
        int adjustment,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken) =>
        interpreter.RunNicedAsync(session, arguments, adjustment, standardInput, standardOutput, standardError, options, cancellationToken, niceElevation);

    /// <summary>
    /// Requires a shell identifier, <c>[A-Za-z_][A-Za-z0-9_]*</c> (ADR 0050, section 3): the only
    /// names <c>$NAME</c> can read back, as bash's <c>export</c> enforces.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a valid identifier.</exception>
    internal static void RequireIdentifier(string name)
    {
        if (!IsIdentifier(name))
        {
            throw new ArgumentException($"'{name}' is not a valid identifier.", nameof(name));
        }
    }

    /// <summary>Whether <paramref name="name"/> is a valid shell variable name.</summary>
    internal static bool IsIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
