using Kazo.Os.AppSdk;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// An in-memory <see cref="IShellSession"/> for unit tests of commands that declare
/// <c>shell.control</c> (ADR 0051): records every call, accepts any directory and runs no command.
/// The real behaviour is covered end to end by the shell interpreter tests.
/// </summary>
public sealed class FakeShellSession : IShellSession
{
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    /// <summary>Creates a shell in <paramref name="workingDirectory"/> with <c>HOME</c> set.</summary>
    public FakeShellSession(string workingDirectory = "/home/user", string home = "/home/user")
    {
        WorkingDirectory = workingDirectory;
        _environment["HOME"] = home;
        _environment["PWD"] = workingDirectory;
    }

    /// <inheritdoc />
    public string WorkingDirectory { get; private set; }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> Environment => new Dictionary<string, string>(_environment, StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyList<string> History { get; init; } = [];

    /// <summary>Gets or sets the commands <see cref="GetCommands()"/> returns.</summary>
    public IReadOnlyList<ShellCommandInfo> Commands { get; init; } = [];

    /// <summary>Gets the argument lists passed to <see cref="RunElevatedAsync"/>.</summary>
    public List<IReadOnlyList<string>> ElevatedRuns { get; } = [];

    /// <summary>Gets or sets the exit code <see cref="RunElevatedAsync"/> returns.</summary>
    public int ElevatedExitCode { get; init; }

    /// <inheritdoc />
    public ValueTask ChangeDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        string target = path.StartsWith('/') ? path : $"{WorkingDirectory.TrimEnd('/')}/{path}";
        _environment["OLDPWD"] = WorkingDirectory;
        WorkingDirectory = target;
        _environment["PWD"] = target;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void SetVariable(string name, string value)
    {
        if (string.IsNullOrEmpty(name) || char.IsAsciiDigit(name[0]) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException($"'{name}' is not a valid identifier.", nameof(name));
        }

        _environment[name] = value;
    }

    /// <inheritdoc />
    public void UnsetVariable(string name) => _environment.Remove(name);

    /// <inheritdoc />
    public IReadOnlyList<ShellCommandInfo> GetCommands() => Commands;

    /// <summary>Gets the shell's <c>$?</c> before the command ran, kept by a bare <c>exit</c>.</summary>
    public int LastExitStatus { get; init; }

    /// <summary>Gets the status <c>exit</c> asked for, or <see langword="null"/> when it was not called.</summary>
    public int? ExitRequested { get; private set; }

    public int Exit(int? exitStatus)
    {
        int status = (((exitStatus ?? LastExitStatus) % 256) + 256) % 256;
        ExitRequested = status;
        return status;
    }

    /// <inheritdoc />
    public ValueTask<int> RunElevatedAsync(
        IReadOnlyList<string> arguments, TextWriter standardOutput, TextWriter standardError, CancellationToken cancellationToken)
    {
        ElevatedRuns.Add([.. arguments]);
        return ValueTask.FromResult(ElevatedExitCode);
    }
}
